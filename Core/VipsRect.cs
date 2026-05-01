using System;

namespace CosmoImage.Core;

public struct VipsRect
{
    public int Left;
    public int Top;
    public int Width;
    public int Height;

    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public VipsRect(int left, int top, int width, int height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Includes(int x, int y)
    {
        return x >= Left && x < Right && y >= Top && y < Bottom;
    }

    public static VipsRect Intersect(VipsRect a, VipsRect b)
    {
        int left = Math.Max(a.Left, b.Left);
        int top = Math.Max(a.Top, b.Top);
        int right = Math.Min(a.Right, b.Right);
        int bottom = Math.Min(a.Bottom, b.Bottom);

        return new VipsRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
