// Decodes an audio file with the repository's own loader (SpeechLib.Audio.AudioUtils.LoadFile: Media Foundation + the repo's
// anti-aliased StreamingResampler) to 16 kHz mono float32 and writes an IEEE-float WAV (format tag 3) so the exact samples survive.
using System.Text;
using SpeechLib.Audio;
var x = AudioUtils.LoadFile(args[0], 16000);
using var w = new BinaryWriter(File.Create(args[1]));
int bytes = x.Length * 4;
w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(16000); w.Write(64000); w.Write((short)4); w.Write((short)32);
w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
var b = new byte[bytes]; Buffer.BlockCopy(x, 0, b, 0, bytes); w.Write(b);
double peak = x.Max(Math.Abs);
Console.WriteLine($"{x.Length} samples {x.Length / 16000.0:F3} s peak {peak:F4} ({20 * Math.Log10(peak):F1} dBFS)");
