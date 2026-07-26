// Custom CPU kernel for the ONNX opset-24 `Swish` operator (ai.onnx domain).
//
// ORT 1.25 supports opset 24 at the graph level but ships NO CPU kernel for
// Swish-24, so models exported with target_opset=24 fail to load on the CPU
// execution provider with a "Missing Kernel" error. This shared library
// registers a CPU implementation that overrides the standard ai.onnx domain.
//
// Swish-24:  Y = X * sigmoid(alpha * X)   (alpha is a float attribute, default 1.0)
//
// The library is loaded at runtime via SessionOptions.RegisterCustomOpsLibrary()
// (see Native/CustomOpLibrary.cs). Loaded once per process; a session config
// flag (session.use_swish_custom_op) in genai_config.json gates the load.

#include "onnxruntime_cxx_api.h"

#include <cmath>
#include <vector>

namespace {

struct SwishKernel {
    // ORT calls the TOp::CreateKernel callback, which constructs the kernel
    // with the raw OrtKernelInfo* (see onnxruntime custom_op_utils.h).
    explicit SwishKernel(const OrtKernelInfo* info) {
        // The `alpha` attribute is optional per the ONNX spec (default 1.0f).
        Ort::ConstKernelInfo kernel_info{info};
        try {
            alpha_ = kernel_info.GetAttribute<float>("alpha");
        } catch (...) {
            alpha_ = 1.0f;
        }
    }

    void Compute(OrtKernelContext* context) {
        Ort::KernelContext ctx(context);

        // 1. Input tensor X.
        Ort::ConstValue input = ctx.GetInput(0);
        auto shape_info = input.GetTensorTypeAndShapeInfo();
        const int64_t element_count = shape_info.GetElementCount();
        const float* X = input.GetTensorData<float>();

        // 2. Output tensor Y with the same shape.
        std::vector<int64_t> dimensions = shape_info.GetShape();
        Ort::UnownedValue output = ctx.GetOutput(0, dimensions.data(), dimensions.size());
        float* Y = output.GetTensorMutableData<float>();

        // 3. Y = X * sigmoid(alpha * X), parallelized over the ORT thread pool.
        //    KernelContext::ParallelFor(fn, total, num_batch, usr) invokes
        //    fn(usr, i) for i in [0, total) on the session's intra-op threads
        //    (the same pool the built-in kernels use). We process one chunk of
        //    kChunkSize elements per invocation to amortize the dispatch cost.
        const float alpha = alpha_;
        if (element_count < kParallelThreshold) {
            ComputeRange(X, Y, 0, element_count, alpha);
            return;
        }

        struct Payload {
            const float* X;
            float* Y;
            int64_t total;
            float alpha;
        } payload{X, Y, element_count, alpha};

        const int64_t num_chunks = (element_count + kChunkSize - 1) / kChunkSize;
        ctx.ParallelFor(
            [](void* user_data, size_t chunk_index) {
                const auto* p = static_cast<const Payload*>(user_data);
                const int64_t begin = static_cast<int64_t>(chunk_index) * kChunkSize;
                const int64_t end =
                    (begin + kChunkSize < p->total) ? begin + kChunkSize : p->total;
                ComputeRange(p->X, p->Y, begin, end, p->alpha);
            },
            static_cast<size_t>(num_chunks),
            /*num_batch=*/0,  // let ORT pick the batch count from its thread pool
            &payload);
    }

private:
    // Below this size the ParallelFor dispatch overhead exceeds the gain.
    static constexpr int64_t kParallelThreshold = 1 << 14;  // 16K elements
    // Elements handled per ParallelFor invocation (~cache-friendly chunk).
    static constexpr int64_t kChunkSize = 1 << 12;  // 4K floats = 16 KB

    static inline void ComputeRange(const float* X, float* Y, int64_t begin, int64_t end,
                                    float alpha) {
        // Numerically stable sigmoid: for ax >= 0 use 1/(1+exp(-ax)),
        // for ax < 0 use e/(1+e) with e = exp(ax) — avoids exp() overflow for
        // large |alpha*x| and keeps the same branchless-per-sign cost profile.
        // The loop body is auto-vectorizable (AVX2 via /arch:AVX2, /O2).
        for (int64_t i = begin; i < end; ++i) {
            const float x = X[i];
            const float ax = alpha * x;
            float sig;
            if (ax >= 0.0f) {
                sig = 1.0f / (1.0f + std::exp(-ax));
            } else {
                const float e = std::exp(ax);
                sig = e / (1.0f + e);
            }
            Y[i] = x * sig;
        }
    }

    float alpha_;
};

struct SwishCustomOp : Ort::CustomOpBase<SwishCustomOp, SwishKernel> {
    // Factory invoked by ORT to create one kernel instance per graph node.
    void* CreateKernel(const OrtApi& /*api*/, const OrtKernelInfo* info) const {
        return new SwishKernel(info);
    }

    // IMPORTANT: standard ONNX op name — this overrides the missing built-in kernel.
    const char* GetName() const { return "Swish"; }
    const char* GetExecutionProviderType() const { return "CPUExecutionProvider"; }

    ONNXTensorElementDataType GetInputType(size_t /*index*/) const {
        return ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT;
    }
    ONNXTensorElementDataType GetOutputType(size_t /*index*/) const {
        return ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT;
    }

    size_t GetInputTypeCount() const { return 1; }
    size_t GetOutputTypeCount() const { return 1; }
};

SwishCustomOp c_SwishCustomOp;

}  // namespace

// Entry point invoked by ORT when the library is registered via
// SessionOptions.RegisterCustomOpsLibrary / OrtRegisterCustomOpsLibrary.
// __declspec(dllexport) is required on MSVC — without it the symbol is not
// visible to GetProcAddress and RegisterCustomOpsLibrary fails.
extern "C" __declspec(dllexport) OrtStatus* ORT_API_CALL
RegisterCustomOps(OrtSessionOptions* options, const OrtApiBase* api_base) {
    const OrtApi* api = api_base->GetApi(ORT_API_VERSION);

    // Empty string == the standard `ai.onnx` domain, so this kernel replaces
    // the missing built-in Swish-24 CPU kernel.
    Ort::CustomOpDomain domain{""};
    domain.Add(&c_SwishCustomOp);

    return api->AddCustomOpDomain(options, domain.release());
}
