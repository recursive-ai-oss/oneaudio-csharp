namespace OneAudio.Devices;

/// <summary>
/// Channel-count conversion between what a program asked for and what a device runs. The rule is the same in both directions and keeps
/// the common cases natural: equal counts copy; a mono source feeds the first two destination channels (a speaker pair) and the rest stay
/// silent; any other source fills the leading destination channels and leaves the rest silent; fewer destination channels than source
/// channels keeps the leading ones, except a mono destination, which receives the average of all source channels.
/// </summary>
internal static class AppleChannelMap
{
    /// <summary>Converts <paramref name="frames"/> interleaved frames. Both spans must hold at least that many frames at their channel counts.</summary>
    public static void Convert(ReadOnlySpan<float> source, int sourceChannels, Span<float> destination, int destinationChannels, int frames)
    {
        if (sourceChannels == destinationChannels)
        {
            source[..(frames * sourceChannels)].CopyTo(destination);
            return;
        }

        var src = source[..(frames * sourceChannels)];
        var dst = destination[..(frames * destinationChannels)];
        if (destinationChannels == 1)
        {
            float scale = 1f / sourceChannels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                var row = src.Slice(f * sourceChannels, sourceChannels);
                for (int c = 0; c < row.Length; c++)
                {
                    sum += row[c];
                }

                dst[f] = sum * scale;
            }

            return;
        }

        dst.Clear();
        int shared = Math.Min(sourceChannels, destinationChannels);
        for (int f = 0; f < frames; f++)
        {
            var from = src.Slice(f * sourceChannels, sourceChannels);
            var to = dst.Slice(f * destinationChannels, destinationChannels);
            if (sourceChannels == 1)
            {
                to[0] = from[0];
                to[1] = from[0];
            }
            else
            {
                from[..shared].CopyTo(to);
            }
        }
    }
}
