// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EUVA.Core.Models;

namespace EUVA.UI.Controls;
public class ByteMinimapControl : FrameworkElement
{
    
    private WriteableBitmap? _bitmap;
    private uint[] _backBuffer = Array.Empty<uint>();
    private int _bmpW;
    private int _bmpH;
    private readonly Image _image = new() { Stretch = Stretch.None };
    private uint[] _mapStrip = Array.Empty<uint>();
    private int _mapStripHeight;          
    private long _mapFileLength;          
    private long _scrollLine;
    private int _visibleLines;
    private int _bytesPerLine = 24;
    private bool _isDragging;
    private Func<long, byte[], int, int>? _readBytes;
    private long _fileLength;
    private bool _entropyMode;
    public bool EntropyMode
    {
        get => _entropyMode;
        set { _entropyMode = value; ScheduleStripRebuild(); }
    }
    private CancellationTokenSource? _buildCts;
    private volatile bool _stripReady;
    private bool _needsFullRedraw = true;
    private uint _colBackground;
    private uint _colViewport;
    private uint _colViewportBorder;
    private uint _colByteNull;
    private uint _colByteFF;
    private uint _colByteControl;
    private uint _colByteWhitespace;
    private uint _colByteDigit;
    private uint _colByteAlpha;
    private uint _colByteSymbol;
    private uint _colByteHigh;
    private const int TopPad = 2;
    private const int BotPad = 2;
    private const int SidePad = 1;
    public event EventHandler<long>? NavigateRequested;
    private double _pixelsPerDip = 1.0;
    
    public ByteMinimapControl()
    {
        ClipToBounds = true;
        AddVisualChild(_image);
        AddLogicalChild(_image);

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RefreshColors();
            if (_fileLength > 0) ScheduleStripRebuild();
        };

        SizeChanged += (_, _) =>
        {
            int w = (int)ActualWidth;
            int h = (int)ActualHeight;
            if (w > 0 && h > 0)
            {
                ResizeBitmap(w, h);
                ScheduleStripRebuild();
            }
        };
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _image;

    protected override Size MeasureOverride(Size a)
    {
        double w = double.IsInfinity(a.Width) ? 60 : a.Width;
        double h = double.IsInfinity(a.Height) ? 800 : a.Height;
        _image.Measure(new Size(w, h));
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size s)
    {
        _image.Arrange(new Rect(s));
        return s;
    }


    public void SetDataSource(Func<long, byte[], int, int> readFunc, long fileLength)
    {
        _readBytes = readFunc;
        _fileLength = fileLength;
        ScheduleStripRebuild();
    }

    public void UpdateViewport(long scrollLine, int visibleLines, int bytesPerLine)
    {
        _scrollLine = scrollLine;
        _visibleLines = visibleLines;
        _bytesPerLine = bytesPerLine;
        InvalidateVisual();
    }

    public void RefreshColors()
    {
        _colBackground     = CArgb(ThemeColor("Hex_Background",     Color.FromRgb(0x1E, 0x1E, 0x2E)));
        _colViewport       = CArgb(Color.FromArgb(0x38, 0xCD, 0xD6, 0xF4));
        _colViewportBorder = CArgb(Color.FromArgb(0xC0, 0x89, 0xB4, 0xFA));

        _colByteNull       = CArgb(ThemeColor("Hex_ByteNull",       Color.FromRgb(0x45, 0x47, 0x5A)));
        _colByteFF         = CArgb(Color.FromRgb(0x85, 0x8A, 0xA0));
        _colByteControl    = CArgb(Color.FromRgb(0xC8, 0x78, 0x82));
        _colByteWhitespace = CArgb(Color.FromRgb(0xBB, 0xAC, 0x65));
        _colByteDigit      = CArgb(Color.FromRgb(0x72, 0xD0, 0xC6));
        _colByteAlpha      = CArgb(Color.FromRgb(0x7A, 0xAA, 0xF0));
        _colByteSymbol     = CArgb(Color.FromRgb(0xB4, 0xB8, 0xD8));
        _colByteHigh       = CArgb(Color.FromRgb(0xB4, 0x96, 0xE8));

        if (_fileLength > 0) ScheduleStripRebuild();
    }

    private void ScheduleStripRebuild()
    {
        _buildCts?.Cancel();
        _buildCts = new CancellationTokenSource();
        var ct = _buildCts.Token;

        int mapH = MapHeight();
        if (mapH <= 0 || _fileLength <= 0 || _readBytes == null) return;

        _stripReady = false;

        var readFn = _readBytes;
        long fileLen = _fileLength;
        bool entropy = _entropyMode;

        
        uint cNull = _colByteNull, cFF = _colByteFF, cCtrl = _colByteControl,
             cWs = _colByteWhitespace, cDig = _colByteDigit, cAlpha = _colByteAlpha,
             cSym = _colByteSymbol, cHigh = _colByteHigh, cBg = _colBackground;

        Task.Run(() =>
        {
            try
            {
                var strip = BuildStrip(readFn, fileLen, mapH, entropy,
                    cNull, cFF, cCtrl, cWs, cDig, cAlpha, cSym, cHigh, cBg, ct);

                if (ct.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(() =>
                {
                    _mapStrip = strip;
                    _mapStripHeight = mapH;
                    _mapFileLength = fileLen;
                    _stripReady = true;
                    _needsFullRedraw = true;
                    InvalidateVisual();
                });
            }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }


    private static uint[] BuildStrip(
        Func<long, byte[], int, int> readFn, long fileLen, int mapH, bool entropyMode,
        uint cNull, uint cFF, uint cCtrl, uint cWs, uint cDig, uint cAlpha,
        uint cSym, uint cHigh, uint cBg,
        CancellationToken ct)
    {
        var strip = new uint[mapH];
        double bytesPerRow = (double)fileLen / mapH;
        int readBufSize = (int)Math.Min(Math.Max(bytesPerRow, 1), 256 * 1024);
        
        if (entropyMode) readBufSize = (int)Math.Min(Math.Max(bytesPerRow, 256), 256 * 1024);

        byte[] buf = ArrayPool<byte>.Shared.Rent(readBufSize);
        try
        {
            for (int row = 0; row < mapH; row++)
            {
                if (ct.IsCancellationRequested) return strip;

                long startOffset = (long)(row * bytesPerRow);
                long endOffset = (long)((row + 1) * bytesPerRow);
                endOffset = Math.Min(endOffset, fileLen);
                long chunkLen = endOffset - startOffset;
                if (chunkLen <= 0) { strip[row] = cBg; continue; }

                
                int toRead;
                long readOffset;
                if (chunkLen <= readBufSize)
                {
                    toRead = (int)chunkLen;
                    readOffset = startOffset;
                }
                else
                {
                    
                    toRead = readBufSize;
                    readOffset = startOffset + (chunkLen - toRead) / 2;
                }

                int actualRead = readFn(readOffset, buf, toRead);
                if (actualRead <= 0) { strip[row] = cBg; continue; }

                if (entropyMode)
                {
                    
                    double ent = CalcEntropy(buf, actualRead);
                    strip[row] = EntropyToColor(ent);
                }
                else
                {
                    
                    strip[row] = AverageByteColors(buf, actualRead,
                        cNull, cFF, cCtrl, cWs, cDig, cAlpha, cSym, cHigh);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }

        return strip;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AverageByteColors(byte[] data, int count,
        uint cNull, uint cFF, uint cCtrl, uint cWs, uint cDig, uint cAlpha,
        uint cSym, uint cHigh)
    {
        long rSum = 0, gSum = 0, bSum = 0;
        for (int i = 0; i < count; i++)
        {
            uint c = GetByteColorStatic(data[i], cNull, cFF, cCtrl, cWs, cDig, cAlpha, cSym, cHigh);
            rSum += (c >> 16) & 0xFF;
            gSum += (c >> 8) & 0xFF;
            bSum += c & 0xFF;
        }
        byte r = (byte)(rSum / count);
        byte g = (byte)(gSum / count);
        byte b = (byte)(bSum / count);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetByteColorStatic(byte v,
        uint cNull, uint cFF, uint cCtrl, uint cWs, uint cDig, uint cAlpha,
        uint cSym, uint cHigh)
    {
        if (v == 0x00) return cNull;
        if (v == 0xFF) return cFF;
        if (v == 0x20) return cWs;
        if (v < 0x09)  return cCtrl;
        if (v <= 0x0D) return cWs;
        if (v < 0x20)  return cCtrl;
        if (v == 0x7F) return cCtrl;
        if (v > 0x7F)  return cHigh;
        if (v >= 0x30 && v <= 0x39) return cDig;
        if (v >= 0x41 && v <= 0x5A) return cAlpha;
        if (v >= 0x61 && v <= 0x7A) return cAlpha;
        return cSym;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CalcEntropy(byte[] data, int count)
    {
        if (count <= 0) return 0;
        Span<int> freq = stackalloc int[256];
        freq.Clear();
        for (int i = 0; i < count; i++) freq[data[i]]++;
        double entropy = 0;
        double logN = Math.Log(2);
        for (int i = 0; i < 256; i++)
        {
            if (freq[i] == 0) continue;
            double p = (double)freq[i] / count;
            entropy -= p * Math.Log(p) / logN;
        }
        return entropy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint EntropyToColor(double entropy)
    {
        
        double t = Math.Clamp(entropy / 8.0, 0.0, 1.0);

        
        byte r, g, b;
        if (t < 0.25)
        {
            double s = t / 0.25;
            r = (byte)(0x18 + s * (0x31 - 0x18));
            g = (byte)(0x18 + s * (0x32 - 0x18));
            b = (byte)(0x25 + s * (0x8A - 0x25));
        }
        else if (t < 0.5)
        {
            double s = (t - 0.25) / 0.25;
            r = (byte)(0x31 + s * (0x04 - 0x31));
            g = (byte)(0x32 + s * (0xD0 - 0x32));
            b = (byte)(0x8A + s * (0xC6 - 0x8A));
        }
        else if (t < 0.75)
        {
            double s = (t - 0.5) / 0.25;
            r = (byte)(0x04 + s * (0xF9 - 0x04));
            g = (byte)(0xD0 + s * (0xE2 - 0xD0));
            b = (byte)(0xC6 + s * (0xAF - 0xC6));
        }
        else
        {
            double s = (t - 0.75) / 0.25;
            r = (byte)(0xF9 + s * (0xF3 - 0xF9));
            g = (byte)(0xE2 + s * (0x8B - 0xE2));
            b = (byte)(0xAF + s * (0xA8 - 0xAF));
        }
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap == null || _bmpW == 0 || _bmpH == 0) return;

        if (_fileLength <= 0 || _readBytes == null)
        {
            FillBackground(_backBuffer, _bmpW, _bmpH, _colBackground);
            FlushBitmap();
            return;
        }

        RenderFrame();
        FlushBitmap();
    }

    private void RenderFrame()
    {
        FillBackground(_backBuffer, _bmpW, _bmpH, _colBackground);

        int mapH = MapHeight();
        int mapW = _bmpW - SidePad * 2;
        if (mapH <= 0 || mapW <= 0) return;

        
        if (_stripReady && _mapStrip.Length >= mapH)
        {
            for (int row = 0; row < mapH; row++)
            {
                uint color = _mapStrip[row];
                int y = TopPad + row;
                if (y >= _bmpH) break;
                FillRect(_backBuffer, _bmpW, SidePad, y, mapW, 1, color);
            }
        }

        
        RenderViewport(mapH);
    }

    private void RenderViewport(int mapH)
    {
        if (_fileLength <= 0 || _bytesPerLine <= 0) return;

        long totalLines = (_fileLength + _bytesPerLine - 1) / _bytesPerLine;
        if (totalLines <= 0) return;

        double vpTop = (double)_scrollLine / totalLines;
        double vpBot = (double)(_scrollLine + _visibleLines) / totalLines;
        vpBot = Math.Min(vpBot, 1.0);

        int y0 = TopPad + (int)(vpTop * mapH);
        int y1 = TopPad + (int)(vpBot * mapH);
        y1 = Math.Max(y1, y0 + 3);

        int mapW = _bmpW - SidePad * 2;

        
        FillRect(_backBuffer, _bmpW, SidePad, y0, mapW, y1 - y0, _colViewport);

        
        FillRect(_backBuffer, _bmpW, SidePad, y0, mapW, 1, _colViewportBorder);
        FillRect(_backBuffer, _bmpW, SidePad, y1 - 1, mapW, 1, _colViewportBorder);
        FillRect(_backBuffer, _bmpW, SidePad, y0, 1, y1 - y0, _colViewportBorder);
        FillRect(_backBuffer, _bmpW, SidePad + mapW - 1, y0, 1, y1 - y0, _colViewportBorder);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MapHeight() => Math.Max(0, _bmpH - TopPad - BotPad);


    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            CaptureMouse();
            NavigateFromY(e.GetPosition(this).Y);
            e.Handled = true;
        }
        else if (e.RightButton == MouseButtonState.Pressed)
        {
            
            EntropyMode = !EntropyMode;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            NavigateFromY(e.GetPosition(this).Y);
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void NavigateFromY(double y)
    {
        if (_fileLength <= 0) return;

        int mapH = MapHeight();
        if (mapH <= 0) return;

        double ratio = Math.Clamp((y - TopPad) / mapH, 0.0, 1.0);
        long offset = (long)(ratio * _fileLength);
        if (_bytesPerLine > 0)
            offset = (offset / _bytesPerLine) * _bytesPerLine;
        offset = Math.Clamp(offset, 0, Math.Max(0, _fileLength - 1));

        NavigateRequested?.Invoke(this, offset);
    }

    private void ResizeBitmap(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (w == _bmpW && h == _bmpH) return;
        _bmpW = w;
        _bmpH = h;
        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _backBuffer = new uint[w * h];
        _image.Source = _bitmap;
        _needsFullRedraw = true;
    }

    private unsafe void FlushBitmap()
    {
        if (_bitmap == null) return;
        _bitmap.Lock();
        try
        {
            fixed (uint* src = _backBuffer)
            {
                Buffer.MemoryCopy(src, (void*)_bitmap.BackBuffer,
                    (long)_bmpW * _bmpH * 4, (long)_bmpW * _bmpH * 4);
            }
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bmpW, _bmpH));
        }
        finally { _bitmap.Unlock(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillRect(uint[] buf, int stride,
        int x, int y, int w, int h, uint colorArgb)
    {
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        int x2 = Math.Min(x + w, stride);
        int y2 = Math.Min(y + h, buf.Length / stride);
        if (x >= x2 || y >= y2) return;

        byte srcA = (byte)(colorArgb >> 24);
        if (srcA == 0) return;

        if (srcA == 255)
        {
            for (int row = y; row < y2; row++)
                buf.AsSpan(row * stride + x, x2 - x).Fill(colorArgb);
        }
        else
        {
            byte srcR = (byte)(colorArgb >> 16);
            byte srcG = (byte)(colorArgb >> 8);
            byte srcB = (byte)(colorArgb);
            int invA = 255 - srcA;

            for (int row = y; row < y2; row++)
            {
                int rs = row * stride;
                for (int col = x; col < x2; col++)
                {
                    uint dst = buf[rs + col];
                    buf[rs + col] =
                        0xFF000000u |
                        (uint)((srcR * srcA + (byte)(dst >> 16) * invA + 127) >> 8) << 16 |
                        (uint)((srcG * srcA + (byte)(dst >> 8) * invA + 127) >> 8) << 8 |
                        (uint)((srcB * srcA + (byte)(dst) * invA + 127) >> 8);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillBackground(uint[] buf, int w, int h, uint color)
    {
        buf.AsSpan(0, w * h).Fill(color | 0xFF000000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Color ThemeColor(string key, Color fallback)
    {
        var res = Application.Current?.TryFindResource(key);
        if (res is SolidColorBrush scb) return scb.Color;
        if (res is Color c) return c;
        return fallback;
    }
}
