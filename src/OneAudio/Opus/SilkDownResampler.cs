using static OneAudio.Opus.SilkMath;

namespace OneAudio.Opus;

/// <summary>
/// SILK's down-sampling resampler from 48 kHz to the internal rate (8, 12 or 16 kHz), an AR2 low-pass followed by a
/// symmetric FIR decimator (the filter bank of the RFC 6716 reference encoder). Only the 48 kHz input rate is supported.
/// </summary>
internal sealed class SilkDownResampler
{
    private const int MaxBatchMs = 10;
    private const int FirOrder = 36;

    // Coefficient sets: two AR2 taps followed by the 18 distinct FIR taps (the filter is symmetric).
    private static readonly short[] Coefs1To3 =
    [
        16102, -15162,
        -13, 0, 20, 26, 5, -31, -43, -4, 65, 90, 7, -157, -248, -44, 593, 1583, 2612, 3271,
    ];

    private static readonly short[] Coefs1To4 =
    [
        22500, -15099,
        3, -14, -20, -15, 2, 25, 37, 25, -16, -71, -107, -79, 50, 292, 623, 982, 1288, 1464,
    ];

    private static readonly short[] Coefs1To6 =
    [
        27540, -15257,
        17, 12, 8, 1, -10, -22, -30, -32, -22, 3, 44, 100, 168, 243, 317, 381, 429, 455,
    ];

    private readonly int[] _sIir = new int[2];
    private readonly int[] _sFir = new int[FirOrder];
    private readonly short[] _delayBuf = new short[48];
    private readonly int[] _buf = new int[48 * MaxBatchMs + FirOrder];
    private short[] _coefs = Coefs1To3;
    private int _invRatioQ16;
    private int _fsInKHz = 48;
    private int _fsOutKHz;
    private int _inputDelay;
    private int _batchSize;

    /// <summary>Configures the resampler for 48 kHz in and the given output rate.</summary>
    public void Init(int fsOutHz)
    {
        Array.Clear(_sIir);
        Array.Clear(_sFir);
        Array.Clear(_delayBuf);
        const int fsIn = 48000;
        switch (fsOutHz)
        {
            case 8000: _inputDelay = 18; _coefs = Coefs1To6; break;
            case 12000: _inputDelay = 10; _coefs = Coefs1To4; break;
            case 16000: _inputDelay = 12; _coefs = Coefs1To3; break;
            default: throw new ArgumentOutOfRangeException(nameof(fsOutHz));
        }
        _fsInKHz = 48;
        _fsOutKHz = fsOutHz / 1000;
        _batchSize = _fsInKHz * MaxBatchMs;
        _invRatioQ16 = ((fsIn << 14) / fsOutHz) << 2;
        while (SMulWW(_invRatioQ16, fsOutHz) < fsIn) _invRatioQ16++;
    }

    /// <summary>Copies the complete state of another resampler (a mono stream turning stereo).</summary>
    public void CopyStateFrom(SilkDownResampler o)
    {
        Array.Copy(o._sIir, _sIir, _sIir.Length);
        Array.Copy(o._sFir, _sFir, _sFir.Length);
        Array.Copy(o._delayBuf, _delayBuf, _delayBuf.Length);
        _coefs = o._coefs; _invRatioQ16 = o._invRatioQ16; _fsInKHz = o._fsInKHz; _fsOutKHz = o._fsOutKHz;
        _inputDelay = o._inputDelay; _batchSize = o._batchSize;
    }

    /// <summary>Resamples <paramref name="inLen"/> samples (at least 48) into <paramref name="output"/>.</summary>
    public void Resample(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        int nSamples = _fsInKHz - _inputDelay;
        input.Slice(0, nSamples).CopyTo(_delayBuf.AsSpan(_inputDelay));
        Down(output, _delayBuf, _fsInKHz);
        Down(output.Slice(_fsOutKHz), input.Slice(nSamples), inLen - _fsInKHz);
        input.Slice(inLen - _inputDelay, _inputDelay).CopyTo(_delayBuf);
    }

    private void Down(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        var buf = _buf;
        Array.Copy(_sFir, buf, FirOrder);
        var c = _coefs;
        int nSamplesIn, outPos = 0;
        while (true)
        {
            nSamplesIn = Math.Min(inLen, _batchSize);
            // silk_resampler_private_AR2
            for (int k = 0; k < nSamplesIn; k++)
            {
                int out32 = _sIir[0] + (input[k] << 8);
                buf[FirOrder + k] = out32;
                out32 <<= 2;
                _sIir[0] = SMlaWB(_sIir[1], out32, c[0]);
                _sIir[1] = SMulWB(out32, c[1]);
            }
            int maxIndexQ16 = nSamplesIn << 16;
            for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += _invRatioQ16)
            {
                int p = indexQ16 >> 16;
                int res = SMulWB(buf[p] + buf[p + 35], c[2]);
                for (int j = 1; j < 18; j++)
                    res = SMlaWB(res, buf[p + j] + buf[p + 35 - j], c[2 + j]);
                output[outPos++] = (short)Sat16(RShiftRound(res, 6));
            }
            input = input.Slice(nSamplesIn);
            inLen -= nSamplesIn;
            if (inLen > 1) Array.Copy(buf, nSamplesIn, buf, 0, FirOrder);
            else break;
        }
        Array.Copy(buf, nSamplesIn, _sFir, 0, FirOrder);
    }
}
