using RustNet.Core;

namespace RustNet.AI.TensorFlow;

/// <summary>Tensor element types, numbered as TensorFlow Lite numbers them.</summary>
public static class TensorType
{
    public const int Float32 = 0;
    public const int Int32 = 2;
    public const int UInt8 = 3;
    public const int Int64 = 4;
    public const int Bool = 6;
    public const int Int16 = 7;
    public const int Int8 = 9;
}

/// <summary>The runtime's TensorFlow Lite interpreter. Use <see cref="Interpreter"/> instead.</summary>
public static class Native
{
    [InternalCall] public static int Create(byte[] model) => throw new RuntimeOnlyException();
    [InternalCall] public static void Dispose(int handle) => throw new RuntimeOnlyException();
    [InternalCall] public static int InputCount(int handle) => throw new RuntimeOnlyException();
    [InternalCall] public static int OutputCount(int handle) => throw new RuntimeOnlyException();
    [InternalCall] public static int OperatorCount(int handle) => throw new RuntimeOnlyException();
    [InternalCall] public static byte[] InputShape(int handle, int index) => throw new RuntimeOnlyException();
    [InternalCall] public static byte[] OutputShape(int handle, int index) => throw new RuntimeOnlyException();
    [InternalCall] public static int InputType(int handle, int index) => throw new RuntimeOnlyException();
    [InternalCall] public static int OutputType(int handle, int index) => throw new RuntimeOnlyException();
    [InternalCall] public static void SetInput(int handle, int index, byte[] raw) => throw new RuntimeOnlyException();
    [InternalCall] public static void SetInputFloat(int handle, int index, byte[] floatsLe) => throw new RuntimeOnlyException();
    [InternalCall] public static void Invoke(int handle) => throw new RuntimeOnlyException();
    [InternalCall] public static byte[] GetOutput(int handle, int index) => throw new RuntimeOnlyException();
    [InternalCall] public static byte[] GetOutputFloat(int handle, int index) => throw new RuntimeOnlyException();
}

/// <summary>
/// A TensorFlow Lite model on the device. Load the <c>.tflite</c> bytes —
/// typically embedded with RustNet.Resources — then set inputs, call
/// <see cref="Invoke"/> and read outputs.
///
/// Float32 and int8/uint8 (per-tensor and per-channel quantised) models run.
/// Inputs and outputs are available either raw (bytes in the tensor's own
/// type, for int8 pipelines) or as real values (quantised and dequantised for
/// you). The supported operators are those most microcontroller models are
/// built from: CONV_2D, DEPTHWISE_CONV_2D, FULLY_CONNECTED, AVERAGE/MAX_POOL_2D,
/// ADD/SUB/MUL/DIV/MAXIMUM/MINIMUM (broadcasting), RELU/RELU6/LOGISTIC/TANH/
/// LEAKY_RELU/HARD_SWISH, SOFTMAX, RESHAPE/SQUEEZE/EXPAND_DIMS, PAD, MEAN,
/// CONCATENATION, QUANTIZE/DEQUANTIZE. A model using anything else is
/// rejected when it is loaded, with the operator named.
/// </summary>
public class Interpreter
{
    private int _handle;

    public Interpreter(byte[] model)
    {
        _handle = Native.Create(model);
    }

    public int InputCount => Native.InputCount(_handle);
    public int OutputCount => Native.OutputCount(_handle);
    public int OperatorCount => Native.OperatorCount(_handle);

    public int[] InputShape(int index) => Ints(Native.InputShape(_handle, index));
    public int[] OutputShape(int index) => Ints(Native.OutputShape(_handle, index));
    public int InputType(int index) => Native.InputType(_handle, index);
    public int OutputType(int index) => Native.OutputType(_handle, index);

    /// <summary>Number of elements in input <paramref name="index"/>.</summary>
    public int InputLength(int index) => Product(InputShape(index));

    /// <summary>Number of elements in output <paramref name="index"/>.</summary>
    public int OutputLength(int index) => Product(OutputShape(index));

    /// <summary>Set an input from real values (quantised for an int8 input).</summary>
    public void SetInput(int index, float[] values)
    {
        byte[] b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            int bits = BitConverter.SingleToInt32Bits(values[i]);
            b[i * 4] = (byte)bits;
            b[i * 4 + 1] = (byte)(bits >> 8);
            b[i * 4 + 2] = (byte)(bits >> 16);
            b[i * 4 + 3] = (byte)(bits >> 24);
        }
        Native.SetInputFloat(_handle, index, b);
    }

    /// <summary>Set an input from its raw bytes in the tensor's own type.</summary>
    public void SetInputRaw(int index, byte[] raw) => Native.SetInput(_handle, index, raw);

    /// <summary>Set an int8 input from signed values.</summary>
    public void SetInputInt8(int index, int[] values)
    {
        byte[] b = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            b[i] = (byte)values[i];
        }
        Native.SetInput(_handle, index, b);
    }

    public void Invoke() => Native.Invoke(_handle);

    /// <summary>An output as real values (dequantised for an int8 output).</summary>
    public float[] GetOutput(int index)
    {
        byte[] b = Native.GetOutputFloat(_handle, index);
        float[] r = new float[b.Length / 4];
        for (int i = 0; i < r.Length; i++)
        {
            int bits = b[i * 4] | (b[i * 4 + 1] << 8) | (b[i * 4 + 2] << 16) | (b[i * 4 + 3] << 24);
            r[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return r;
    }

    /// <summary>An output as raw bytes in the tensor's own type.</summary>
    public byte[] GetOutputRaw(int index) => Native.GetOutput(_handle, index);

    /// <summary>Index of the largest output value — the predicted class of a classifier.</summary>
    public int ArgMax(int index)
    {
        float[] v = GetOutput(index);
        int best = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (v[i] > v[best])
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>Release the model's memory on the device.</summary>
    public void Dispose()
    {
        if (_handle >= 0)
        {
            Native.Dispose(_handle);
            _handle = -1;
        }
    }

    private static int[] Ints(byte[] b)
    {
        int[] r = new int[b.Length / 4];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = b[i * 4] | (b[i * 4 + 1] << 8) | (b[i * 4 + 2] << 16) | (b[i * 4 + 3] << 24);
        }
        return r;
    }

    private static int Product(int[] shape)
    {
        int n = 1;
        for (int i = 0; i < shape.Length; i++)
        {
            n *= shape[i];
        }
        return n;
    }
}
