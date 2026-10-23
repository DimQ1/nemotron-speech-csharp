// voicetype-audio: the audio capture helper of VoiceType on macOS.
//
// The desktop head of the app is plain .NET (no macOS bindings), so capture lives in this small helper inside the app
// bundle (Contents/MacOS/voicetype-audio). The app starts it, reads its stdout and stops it by closing stdin.
//
//   voicetype-audio mic       the default input device (AVAudioEngine); follows a change of the default input
//   voicetype-audio system    what the Mac plays: a global Core Audio process tap (macOS 14.2 or newer); follows a
//                             change of the default output device
//   voicetype-audio check     prints what this Mac supports and exits (CI smoke test; touches no device)
//
// stdout: a little-endian Int32 with the sample rate, then little-endian Float32 mono samples at that rate. When the
//         rate changes (another device), the marker bytes AD DE C0 7F (a NaN no device produces) and the new Int32 rate
//         follow, then samples at the new rate. Reader: VoiceType.Uno.Core, Services/Audio/HelperAudioSource.cs.
// stderr: one line per event or problem. Exit code 0 when stdin closed, 2 on an error (the message says what to do).
//
// Permissions are asked for by macOS on first use and belong to VoiceType.app (Info.plist: NSMicrophoneUsageDescription,
// NSAudioCaptureUsageDescription), because the helper runs as its child process.

import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation

signal(SIGPIPE, SIG_IGN)

func log(_ message: String) {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
}

func fail(_ message: String) -> Never {
    log(message)
    exit(2)
}

/// Writes the rate header once (and a marker + rate when it changes), then mono samples; a closed pipe ends the helper.
final class PcmWriter {
    static let rateChangeMarker: UInt32 = 0x7FC0_DEAD

    private let lock = NSLock()
    private var currentRate: Int32 = 0
    private let output = FileHandle.standardOutput

    func write(sampleRate: Double, samples: UnsafeBufferPointer<Float>) {
        lock.lock()
        defer { lock.unlock() }
        do {
            let rate = Int32(sampleRate.rounded())
            if rate != currentRate {
                var bytes = Data()
                if currentRate != 0 {
                    var marker = PcmWriter.rateChangeMarker.littleEndian
                    bytes.append(Data(bytes: &marker, count: 4))
                }
                var value = rate.littleEndian
                bytes.append(Data(bytes: &value, count: 4))
                try output.write(contentsOf: bytes)
                currentRate = rate
            }
            if samples.count > 0, let base = samples.baseAddress {
                try output.write(contentsOf: Data(bytes: base, count: samples.count * MemoryLayout<Float>.size))
            }
        } catch {
            exit(0)
        }
    }
}

let writer = PcmWriter()

// MARK: - Microphone

final class Microphone {
    private let engine = AVAudioEngine()
    private var observer: NSObjectProtocol?

    func start() {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            break
        case .notDetermined:
            let done = DispatchSemaphore(value: 0)
            AVCaptureDevice.requestAccess(for: .audio) { _ in done.signal() }
            done.wait()
            if AVCaptureDevice.authorizationStatus(for: .audio) != .authorized {
                fail("Microphone access was not allowed. System Settings > Privacy & Security > Microphone: turn on VoiceType.")
            }
        default:
            fail("Microphone access is off for VoiceType. System Settings > Privacy & Security > Microphone: turn on VoiceType.")
        }

        // Another default input (a headset plugged in) stops the engine: start again on the new device.
        observer = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil
        ) { [weak self] _ in
            log("microphone: the input device changed, restarting")
            self?.restart()
        }
        run()
    }

    func stop() {
        engine.stop()
    }

    private func restart() {
        engine.stop()
        engine.inputNode.removeTap(onBus: 0)
        run()
    }

    private func run() {
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        if format.sampleRate <= 0 || format.channelCount == 0 {
            fail("No microphone is available (the default input device has no input format).")
        }

        let rate = format.sampleRate
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
            let frames = Int(buffer.frameLength)
            let channels = Int(buffer.format.channelCount)
            guard frames > 0, channels > 0, let data = buffer.floatChannelData else { return }
            var mono = [Float](repeating: 0, count: frames)
            for c in 0..<channels {
                let channel = data[c]
                for i in 0..<frames { mono[i] += channel[i] }
            }
            if channels > 1 {
                let scale = 1 / Float(channels)
                for i in 0..<frames { mono[i] *= scale }
            }
            mono.withUnsafeBufferPointer { writer.write(sampleRate: rate, samples: $0) }
        }

        do {
            try engine.start()
        } catch {
            fail("The microphone could not be started: \(error.localizedDescription)")
        }
        log("microphone: \(Int(rate)) Hz, \(format.channelCount) channel(s)")
    }
}

// MARK: - System audio (Core Audio process tap)

func address(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
}

func getProperty<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ value: inout T) -> OSStatus {
    var property = address(selector)
    var size = UInt32(MemoryLayout<T>.size)
    return AudioObjectGetPropertyData(object, &property, 0, nil, &size, &value)
}

/// The UID of the current default output device; nil when there is none.
func defaultOutputDeviceUID() -> String? {
    var device = AudioObjectID(kAudioObjectUnknown)
    if getProperty(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultOutputDevice, &device) != noErr
        || device == kAudioObjectUnknown {
        return nil
    }
    var uid: CFString = "" as CFString
    if getProperty(device, kAudioDevicePropertyDeviceUID, &uid) != noErr {
        return nil
    }
    return uid as String
}

@available(macOS 14.2, *)
final class SystemTap {
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var tapUID = ""
    private var tapChannels = 2
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private var outputUID = ""
    // Audio arrives on ioQueue; device changes and start/stop are serialized on control (AudioDeviceStop waits for the
    // running IO block, so it must never be called from the IO queue).
    private let ioQueue = DispatchQueue(label: "voicetype.system-audio.io", qos: .userInitiated)
    private let control = DispatchQueue(label: "voicetype.system-audio.control")
    private var listener: AudioObjectPropertyListenerBlock?

    func start() {
        // Everything the Mac plays, mixed to stereo and not muted for the user.
        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        description.uuid = UUID()
        description.muteBehavior = .unmuted
        tapUID = description.uuid.uuidString

        let status = AudioHardwareCreateProcessTap(description, &tapID)
        if status != noErr {
            fail("System audio could not be captured (\(status)). System Settings > Privacy & Security > "
                + "Screen & System Audio Recording: allow VoiceType under \"System Audio Recording Only\".")
        }

        control.sync { startDevice() }

        // Headphones plugged in or another output picked: the tap stays, the device that clocks it is rebuilt.
        var property = address(kAudioHardwarePropertyDefaultOutputDevice)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self, let uid = defaultOutputDeviceUID(), uid != self.outputUID else { return }
            log("system audio: the output device changed, restarting")
            self.stopDevice()
            self.startDevice()
        }
        listener = block
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &property, control, block)
    }

    func stop() {
        if let listener {
            var property = address(kAudioHardwarePropertyDefaultOutputDevice)
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &property, control, listener)
        }
        control.sync { stopDevice() }
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
    }

    /// Creates the private aggregate device (clocked by the default output) that carries the tap as its input. On `control`.
    private func startDevice() {
        var format = AudioStreamBasicDescription()
        var status = getProperty(tapID, kAudioTapPropertyFormat, &format)
        if status != noErr {
            fail("The system audio format could not be read (\(status)).")
        }
        if format.mFormatID != kAudioFormatLinearPCM || (format.mFormatFlags & kAudioFormatFlagIsFloat) == 0 || format.mBitsPerChannel != 32 {
            fail("Unexpected system audio format (id \(format.mFormatID), flags \(format.mFormatFlags), \(format.mBitsPerChannel) bit).")
        }
        tapChannels = max(1, Int(format.mChannelsPerFrame))
        let rate = format.mSampleRate

        guard let output = defaultOutputDeviceUID() else {
            fail("No output device is available, so there is no system audio to capture.")
        }
        outputUID = output
        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: "VoiceType system audio",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: output,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: output]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true, kAudioSubTapUIDKey: tapUID]],
        ]
        status = AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &aggregateID)
        if status != noErr {
            fail("The system audio device could not be created (\(status)).")
        }

        let channels = tapChannels
        status = AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateID, ioQueue) { _, inputData, _, _, _ in
            // The tap's channels come after any input channels of the output device (a headset microphone): take the last ones.
            let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inputData))
            var sources: [(UnsafePointer<Float>, Int, Int)] = []   // (samples, channels in the buffer, channel index)
            var frames = Int.max
            for buffer in buffers {
                guard let data = buffer.mData, buffer.mNumberChannels > 0 else { continue }
                let stride = Int(buffer.mNumberChannels)
                frames = min(frames, Int(buffer.mDataByteSize) / (MemoryLayout<Float>.size * stride))
                for c in 0..<stride {
                    sources.append((UnsafePointer(data.assumingMemoryBound(to: Float.self)), stride, c))
                }
            }
            guard frames != Int.max, frames > 0, !sources.isEmpty else { return }
            let used = sources.suffix(min(channels, sources.count))
            var mono = [Float](repeating: 0, count: frames)
            for (samples, stride, channel) in used {
                for i in 0..<frames { mono[i] += samples[i * stride + channel] }
            }
            let scale = 1 / Float(used.count)
            for i in 0..<frames { mono[i] *= scale }
            mono.withUnsafeBufferPointer { writer.write(sampleRate: rate, samples: $0) }
        }
        if status != noErr {
            fail("The system audio callback could not be installed (\(status)).")
        }

        status = AudioDeviceStart(aggregateID, procID)
        if status != noErr {
            fail("System audio could not be started (\(status)).")
        }
        log("system audio: \(Int(rate)) Hz, \(channels) channel(s), output device \(output)")
    }

    /// Stops and destroys the aggregate device (the tap stays). On `control`.
    private func stopDevice() {
        if let procID {
            AudioDeviceStop(aggregateID, procID)
            AudioDeviceDestroyIOProcID(aggregateID, procID)
            self.procID = nil
        }
        if aggregateID != kAudioObjectUnknown {
            AudioHardwareDestroyAggregateDevice(aggregateID)
            aggregateID = AudioObjectID(kAudioObjectUnknown)
        }
    }
}

// MARK: - Main

let mode = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ""
var stopCapture: () -> Void = {}

switch mode {
case "check":
    let version = ProcessInfo.processInfo.operatingSystemVersion
    var systemAudio = "no (needs macOS 14.2)"
    if #available(macOS 14.2, *) { systemAudio = "yes" }
    print("voicetype-audio: macOS \(version.majorVersion).\(version.minorVersion).\(version.patchVersion), microphone: yes, system audio: \(systemAudio)")
    exit(0)
case "mic":
    let microphone = Microphone()
    microphone.start()
    stopCapture = { microphone.stop() }
case "system":
    if #available(macOS 14.2, *) {
        let tap = SystemTap()
        tap.start()
        stopCapture = { tap.stop() }
    } else {
        fail("Capturing what the Mac plays needs macOS 14.2 or newer.")
    }
default:
    fail("usage: voicetype-audio mic|system|check")
}

// The app stops capture by closing stdin (or by ending the process).
let capture = stopCapture
DispatchQueue.global().async {
    while !FileHandle.standardInput.availableData.isEmpty {}
    capture()
    exit(0)
}

dispatchMain()
