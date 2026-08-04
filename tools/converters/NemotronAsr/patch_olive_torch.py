"""Monkey-patch Olive's k-quant quantization to use torch instead of cupy.
This is needed because cupy's prebuilt wheels don't include sm_120 (RTX 5070 Blackwell).
"""

import numpy as np
import torch

_original_kquant_quantize = None


def _kquant_quantize_torch(data: np.ndarray, num_bits: int = 4, group_size: int = 32) -> tuple:
    """GPU (torch) implementation of k-quant quantization."""
    device = torch.device("cuda")
    data = torch.from_numpy(data).to(device).reshape((-1, group_size)).to(torch.float32)
    maxq = 2**num_bits - 1
    minq = 0

    sum_x2 = torch.sum(data**2, dim=1, keepdim=True)
    av_x = torch.sqrt(sum_x2 / group_size)
    weights = torch.add(av_x, torch.abs(data))

    rmin = torch.min(data, dim=1, keepdim=True)[0]
    rmax = torch.max(data, dim=1, keepdim=True)[0]
    sum_w = torch.sum(weights, dim=1, keepdim=True)
    sum_x = torch.sum(weights * data, dim=1, keepdim=True)

    iscale = torch.ones(rmax.shape, dtype=data.dtype, device=device)
    mask = rmin != rmax
    iscale[mask] = (maxq - minq) / (rmax[mask] - rmin[mask])
    scale = 1.0 / iscale
    quant_data = torch.clip(torch.round(iscale * (data - rmin)), minq, maxq)
    diff = scale * quant_data + rmin - data
    best_mad = torch.sum(weights * diff**2, dim=1, keepdim=True)

    nstep = 20
    rdelta = 0.1
    rrmin = -1.0
    for is_ in range(nstep):
        iscale_new = torch.ones(rmax.shape, dtype=data.dtype, device=device)
        factor = torch.tensor([rrmin + rdelta * is_ + maxq - minq], dtype=data.dtype, device=device)[0]
        mask = rmin != rmax
        iscale_new[mask] = factor / (rmax[mask] - rmin[mask])
        quant_data_new = torch.clip(torch.round(iscale_new * (data - rmin)), minq, maxq)
        mul_weights_quant_data_new = weights * quant_data_new
        sum_l = torch.sum(mul_weights_quant_data_new, dim=1, keepdim=True)
        sum_l2 = torch.sum(mul_weights_quant_data_new * quant_data_new, dim=1, keepdim=True)
        sum_xl = torch.sum(mul_weights_quant_data_new * data, dim=1, keepdim=True)
        D = torch.subtract(sum_w * sum_l2, sum_l**2)

        this_scale = (sum_w * sum_xl - sum_x * sum_l) / D
        this_min = (sum_l2 * sum_x - sum_l * sum_xl) / D

        diff = this_scale * quant_data_new + this_min - data
        mad = torch.sum(weights * diff**2, dim=1, keepdim=True)

        idx_to_replace = torch.where(mad < best_mad)[0]
        quant_data[idx_to_replace, :] = quant_data_new[idx_to_replace, :]
        best_mad[idx_to_replace] = mad[idx_to_replace]
        scale[idx_to_replace] = this_scale[idx_to_replace]
        rmin[idx_to_replace] = this_min[idx_to_replace]

    zero_point = torch.clip(((-rmin) / scale).round(), 0, maxq).to(torch.uint8)
    scale = scale.to(torch.float64)
    q_weight = torch.empty_like(data, dtype=scale.dtype)
    torch.divide(data, scale, out=q_weight)
    torch.add(q_weight, zero_point, out=q_weight)
    torch.round(q_weight, out=q_weight)
    torch.clip(q_weight, minq, maxq, out=q_weight)

    return q_weight.cpu().numpy(), scale.cpu().numpy(), zero_point.cpu().numpy()


def patch_olive():
    """Replace Olive's k-quant CUDA function with torch-based version."""
    import olive.passes.onnx.kquant_quantization as kq

    global _original_kquant_quantize
    _original_kquant_quantize = kq._kquant_quantize

    def patched_kquant_quantize(data, num_bits=4, group_size=32):
        try:
            import torch as t

            if t.cuda.is_available():
                return _kquant_quantize_torch(data, num_bits, group_size)
        except ImportError:
            pass

        try:
            import cupy as cp

            if cp.cuda.runtime.getDeviceCount() > 0:
                return kq._kquant_quantize_cuda(data, num_bits, group_size, cp)
        except Exception:
            pass

        return kq._kquant_quantize_cpu(data, num_bits, group_size)

    kq._kquant_quantize = patched_kquant_quantize
    print("[PATCH] Olive k-quant: torch CUDA enabled for sm_120")


if __name__ == "__main__":
    # Test
    patch_olive()
    from olive.passes.onnx.kquant_quantization import _kquant_quantize
    data = np.random.randn(1024, 32).astype(np.float32)
    qw, sc, zp = _kquant_quantize(data, num_bits=8, group_size=32)
    print(f"Test OK: q_weight={qw.shape}, scale={sc.shape}, zp={zp.shape}")
