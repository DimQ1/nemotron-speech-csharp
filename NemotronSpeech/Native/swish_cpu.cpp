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

        // 3. Y = X * sigmoid(alpha * X) = X / (1 + exp(-alpha * X)).
        //    The loop is auto-vectorizable (AVX2 via /arch:AVX2).
        for (int64_t i = 0; i < element_count; ++i) {
            const float x = X[i];
            Y[i] = x / (1.0f + std::exp(-alpha_ * x));
        }
    }

private:
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
