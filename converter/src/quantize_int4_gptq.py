"""Quantize encoder to INT4 using GPTQ with calibration data for better FP32 fidelity."""
import sys
from pathlib import Path
import numpy as np
import onnx
from onnxruntime.quantization.matmul_nbits_quantizer import MatMulNBitsQuantizer
from onnxruntime.quantization.calibrate import CalibrationDataReader

class AudioCalibrationReader(CalibrationDataReader):
    """Feeds random mel-like data for calibration. Replace with real audio for best results."""
    def __init__(self, num_samples=20):
        self.num_samples = num_samples
        self.idx = 0

    def get_next(self):
        if self.idx >= self.num_samples:
            return None
        self.idx += 1
        # Simulate mel spectrogram: [1, 65, 128] — random audio features
        mel = np.random.randn(1, 65, 128).astype(np.float32) * 0.3
        length = np.array([65], dtype=np.int64)
        cache_ch = np.zeros((1, 24, 70, 1024), dtype=np.float32)
        cache_t = np.zeros((1, 24, 1024, 8), dtype=np.float32)
        cache_len = np.array([0], dtype=np.int64)
        lang = np.array([25], dtype=np.int64)
        return {
            'audio_signal': mel,
            'length': length,
            'cache_last_channel': cache_ch,
            'cache_last_time': cache_t,
            'cache_last_channel_len': cache_len,
            'lang_id': lang,
        }

fp32_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('modules/asr/cpu-opset24-fp32-c056/encoder.onnx')
out_dir = Path(sys.argv[2]) if len(sys.argv) > 2 else Path('modules/asr/cpu-opset24-int4-gptq')
out_dir.mkdir(parents=True, exist_ok=True)

print(f'Loading {fp32_path}')
model = onnx.load(str(fp32_path))

# GPTQ config for weight-only INT4 quantization
from onnxruntime.quantization import GPTQWeightOnlyQuantConfig
gptq_config = GPTQWeightOnlyQuantConfig(
    bits=4,
    group_size=32,           # block_size=32 — same as current
    is_symmetric=True,
    accuracy_level=4,        # INT8 accumulation
    calibration_data_reader=AudioCalibrationReader(num_samples=20),
)

print('Running GPTQ quantization (this may take several minutes)...')
quant = MatMulNBitsQuantizer(
    model,
    block_size=32,
    is_symmetric=True,
    accuracy_level=4,
    algo_config=gptq_config,
)
quant.process()

out_model = out_dir / 'encoder.onnx'
onnx.save_model(
    quant.model.model,
    str(out_model),
    save_as_external_data=True,
    all_tensors_to_one_file=True,
    location='encoder.onnx.data',
)
size_mb = (out_dir / 'encoder.onnx.data').stat().st_size / 1e6
print(f'[ok] GPTQ INT4 encoder -> {out_model} (+data {size_mb:.1f} MB)')
