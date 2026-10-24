using static OneAudio.Opus.SilkMath;

namespace OneAudio.Opus;

/// <summary>
/// SILK's up-sampling resampler from the internal rate (8, 12 or 16 kHz) to the output rate (RFC 6716 section 4.2.9).
/// The decoder is specified through this exact filter, so the arithmetic follows the reference.
/// </summary>
internal sealed class SilkResampler
{
    private const int FirOrder12 = 8;
    private const int MaxBatchInputMs = 10;

    private static readonly short[] Up2Hq0 = [1746, 14986, unchecked((short)(39083 - 65536))];
    private static readonly short[] Up2Hq1 = [6854, 25769, unchecked((short)(55542 - 65536))];

    // Delay in input samples per (internal rate, output rate); rows 8, 12, 16 kHz; columns 8, 12, 16, 24, 48 kHz.
    private static readonly sbyte[] DelayMatrixDec =
    [
        4, 0, 2, 0, 0,
        0, 9, 4, 7, 4,
        0, 3, 12, 7, 7,
    ];

    private enum Kind { Copy, Up2Hq, IirFir }

    private readonly int[] _sIir = new int[6];
    private readonly short[] _sFir = new short[FirOrder12];
    private readonly short[] _delayBuf = new short[48];
    private readonly short[] _buf = new short[2 * MaxBatchInputMs * 16 + FirOrder12];
    private Kind _kind;
    private int _batchSize;
    private int _invRatioQ16;
    private int _fsInKHz;
    private int _fsOutKHz;
    private int _inputDelay;

    public void Clear()
    {
        Array.Clear(_sIir);
        Array.Clear(_sFir);
        Array.Clear(_delayBuf);
        _kind = Kind.Copy;
        _batchSize = _invRatioQ16 = _fsInKHz = _fsOutKHz = _inputDelay = 0;
    }

    /// <summary>Copies the complete state of another resampler (used when a mono stream turns stereo).</summary>
    public void CopyStateFrom(SilkResampler o)
    {
        Array.Copy(o._sIir, _sIir, _sIir.Length);
        Array.Copy(o._sFir, _sFir, _sFir.Length);
        Array.Copy(o._delayBuf, _delayBuf, _delayBuf.Length);
        _kind = o._kind; _batchSize = o._batchSize; _invRatioQ16 = o._invRatioQ16;
        _fsInKHz = o._fsInKHz; _fsOutKHz = o._fsOutKHz; _inputDelay = o._inputDelay;
    }

    private static int RateId(int r) => ((((r >> 12) - (r > 16000 ? 1 : 0)) >> (r > 24000 ? 1 : 0))) - 1;

    /// <summary>Configures the resampler. Output rates below the internal rate are not supported.</summary>
    public void Init(int fsIn, int fsOut)
    {
        Clear();
        if ((fsIn != 8000 && fsIn != 12000 && fsIn != 16000) ||
            (fsOut != 8000 && fsOut != 12000 && fsOut != 16000 && fsOut != 24000 && fsOut != 48000))
            throw new UnsupportedAudioFormatException("Unsupported Opus output sample rate.");
        if (fsOut < fsIn)
            throw new UnsupportedAudioFormatException("Opus decoding below the SILK internal sample rate is not supported; decode at 48 kHz.");

        _inputDelay = DelayMatrixDec[RateId(fsIn) * 5 + RateId(fsOut)];
        _fsInKHz = fsIn / 1000;
        _fsOutKHz = fsOut / 1000;
        _batchSize = _fsInKHz * MaxBatchInputMs;
        int up2x = 0;
        if (fsOut > fsIn)
        {
            if (fsOut == fsIn * 2)
            {
                _kind = Kind.Up2Hq;
            }
            else
            {
                _kind = Kind.IirFir;
                up2x = 1;
            }
        }
        else
        {
            _kind = Kind.Copy;
        }
        _invRatioQ16 = ((fsIn << (14 + up2x)) / fsOut) << 2;
        while (SMulWW(_invRatioQ16, fsOut) < (fsIn << up2x)) _invRatioQ16++;
    }

    /// <summary>Resamples <paramref name="inLen"/> input samples (at least one millisecond) into <paramref name="output"/>.</summary>
    public void Resample(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        int nSamples = _fsInKHz - _inputDelay;
        input.Slice(0, nSamples).CopyTo(_delayBuf.AsSpan(_inputDelay));
        switch (_kind)
        {
            case Kind.Up2Hq:
                Up2Hq(output, _delayBuf, _fsInKHz);
                Up2Hq(output.Slice(_fsOutKHz), input.Slice(nSamples), inLen - _fsInKHz);
                break;
            case Kind.IirFir:
                IirFir(output, _delayBuf, _fsInKHz);
                IirFir(output.Slice(_fsOutKHz), input.Slice(nSamples), inLen - _fsInKHz);
                break;
            default:
                _delayBuf.AsSpan(0, _fsInKHz).CopyTo(output);
                input.Slice(nSamples, inLen - _fsInKHz).CopyTo(output.Slice(_fsOutKHz));
                break;
        }
        input.Slice(inLen - _inputDelay, _inputDelay).CopyTo(_delayBuf);
    }

    private void Up2Hq(Span<short> output, ReadOnlySpan<short> input, int len)
    {
        var s = _sIir;
        for (int k = 0; k < len; k++)
        {
            int in32 = input[k] << 10;

            int y = in32 - s[0];
            int x = SMulWB(y, Up2Hq0[0]);
            int out1 = s[0] + x;
            s[0] = in32 + x;

            y = out1 - s[1];
            x = SMulWB(y, Up2Hq0[1]);
            int out2 = s[1] + x;
            s[1] = out1 + x;

            y = out2 - s[2];
            x = SMlaWB(y, y, Up2Hq0[2]);
            out1 = s[2] + x;
            s[2] = out2 + x;

            output[2 * k] = (short)Sat16(RShiftRound(out1, 10));

            y = in32 - s[3];
            x = SMulWB(y, Up2Hq1[0]);
            out1 = s[3] + x;
            s[3] = in32 + x;

            y = out1 - s[4];
            x = SMulWB(y, Up2Hq1[1]);
            out2 = s[4] + x;
            s[4] = out1 + x;

            y = out2 - s[5];
            x = SMlaWB(y, y, Up2Hq1[2]);
            out1 = s[5] + x;
            s[5] = out2 + x;

            output[2 * k + 1] = (short)Sat16(RShiftRound(out1, 10));
        }
    }

    private void IirFir(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        var buf = _buf;
        Array.Copy(_sFir, buf, FirOrder12);
        int nSamplesIn;
        int outPos = 0;
        while (true)
        {
            nSamplesIn = Math.Min(inLen, _batchSize);
            Up2Hq(buf.AsSpan(FirOrder12), input, nSamplesIn);
            int maxIndexQ16 = nSamplesIn << (16 + 1);
            for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += _invRatioQ16)
            {
                int tableIndex = SMulWB(indexQ16 & 0xFFFF, 12);
                int p = indexQ16 >> 16;
                var t = SilkTables.ResamplerFracFIR12;
                int a = tableIndex * 4, b = (11 - tableIndex) * 4;
                int res = SMulBB(buf[p], t[a]);
                res = SMlaBB(res, buf[p + 1], t[a + 1]);
                res = SMlaBB(res, buf[p + 2], t[a + 2]);
                res = SMlaBB(res, buf[p + 3], t[a + 3]);
                res = SMlaBB(res, buf[p + 4], t[b + 3]);
                res = SMlaBB(res, buf[p + 5], t[b + 2]);
                res = SMlaBB(res, buf[p + 6], t[b + 1]);
                res = SMlaBB(res, buf[p + 7], t[b]);
                output[outPos++] = (short)Sat16(RShiftRound(res, 15));
            }
            input = input.Slice(nSamplesIn);
            inLen -= nSamplesIn;
            if (inLen > 0) Array.Copy(buf, nSamplesIn << 1, buf, 0, FirOrder12);
            else break;
        }
        Array.Copy(buf, nSamplesIn << 1, _sFir, 0, FirOrder12);
    }
}
