namespace TapRoom.Services;

internal static class ScreenshotDifference
{
    internal const int SampleWidth = 160, SampleHeight = 90, Tolerance = 18;

    // Sample RGB only (never alpha). Use an evenly spaced grid and ignore small
    // colour differences. Saved PNGs are full resolution, not the sampled image.
    internal static byte[] Sample(byte[] pixels, int width, int height, int stride)
    {
        var sample = new byte[SampleWidth * SampleHeight * 3];
        int offset = 0;
        for (int y = 0; y < SampleHeight; y++)
        for (int x = 0; x < SampleWidth; x++)
        {
            int sx = (int)((x + 0.5) * width / SampleWidth);
            int sy = (int)((y + 0.5) * height / SampleHeight);
            int index = sy * stride + sx * 4;
            sample[offset++] = pixels[index];
            sample[offset++] = pixels[index + 1];
            sample[offset++] = pixels[index + 2];
        }
        return sample;
    }

    internal static double Percent(byte[] baseline, byte[] current)
    {
        if (baseline.Length != current.Length || current.Length == 0 || current.Length % 3 != 0)
            throw new ArgumentException("Screenshot samples must contain matching RGB triples.");
        int changed = 0;
        for (int i = 0; i < current.Length; i += 3)
            if (Math.Abs(current[i] - baseline[i]) > Tolerance || Math.Abs(current[i + 1] - baseline[i + 1]) > Tolerance ||
                Math.Abs(current[i + 2] - baseline[i + 2]) > Tolerance) changed++;
        return 100.0 * changed / (current.Length / 3);
    }
}
