namespace FlowLedger.Application.Features.LocalAi;

public static class VectorMath
{
    /// <summary>Dot product; equals cosine similarity for the L2-normalised vectors the embedders return.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var sum = 0f;
        var i = 0;
        var width = System.Numerics.Vector<float>.Count;
        if (n >= width)
        {
            var acc = System.Numerics.Vector<float>.Zero;
            for (; i <= n - width; i += width)
            {
                acc += new System.Numerics.Vector<float>(a.Slice(i, width)) * new System.Numerics.Vector<float>(b.Slice(i, width));
            }

            sum = System.Numerics.Vector.Sum(acc);
        }

        for (; i < n; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    public static float[] Normalize(float[] v)
    {
        double norm = 0;
        foreach (var x in v)
        {
            norm += (double)x * x;
        }

        if (norm <= 1e-12)
        {
            return v;
        }

        var inv = (float)(1.0 / Math.Sqrt(norm));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= inv;
        }

        return v;
    }
}
