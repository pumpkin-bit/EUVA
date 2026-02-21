// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EUVA.Core.Models;
using EUVA.UI;

namespace EUVA.UI.Controls;

public class VirtualizedHexView : FrameworkElement
{
    private sealed class GlyphCache
    {
        private readonly Dictionary<long, uint[]> _cache = new(2048);
        private readonly GlyphTypeface _glyphTypeface;
        private readonly double _fontSize;
        private readonly int _cellW; 
        private readonly int _cellH;  
        private readonly double _pixelsPerDip;

        public int CellW => _cellW;
        public int CellH => _cellH;

        public GlyphCache(GlyphTypeface glyphTypeface, double fontSize,
            int cellW, int cellH, double pixelsPerDip)
        {
            _glyphTypeface  = glyphTypeface;
            _fontSize       = fontSize;
            _cellW          = cellW;
            _cellH          = cellH;
            _pixelsPerDip   = pixelsPerDip;
        }
        public uint[] Get(char c, uint colorArgb)
        {
            long key = ((long)(byte)c << 32) | colorArgb;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var pixels = RasterizeGlyph(c, colorArgb);
            _cache[key] = pixels;
            return pixels;
        }

        public void Clear() => _cache.Clear();

        private uint[] RasterizeGlyph(char c, uint colorArgb)
        {
            double dipW = _cellW / _pixelsPerDip;
            double dipH = _cellH / _pixelsPerDip;
            double dpi  = 96.0 * _pixelsPerDip;

            byte r = (byte)(colorArgb >> 16);
            byte g = (byte)(colorArgb >> 8);
            byte b = (byte)(colorArgb);
            var brush = new SolidColorBrush(Color.FromArgb(255, r, g, b));
            brush.Freeze();

            var dv = new DrawingVisual();
            TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Aliased);
            TextOptions.SetTextFormattingMode(dv, TextFormattingMode.Display);

            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, dipW, dipH));
                var tf = new Typeface(new FontFamily("Consolas"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(c.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf, _fontSize, brush, _pixelsPerDip);
                dc.DrawText(ft, new Point(0, 0));
            }

            var rtb = new RenderTargetBitmap(_cellW, _cellH, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(dv);

            int stride = _cellW * 4;
            byte[] raw = new byte[_cellH * stride];
            rtb.CopyPixels(raw, stride, 0);
            var result = new uint[_cellW * _cellH];
            for (int i = 0; i < result.Length; i++)
            {
                byte pb = raw[i * 4 + 0];
                byte pg = raw[i * 4 + 1]; 
                byte pr = raw[i * 4 + 2];
                byte pa = raw[i * 4 + 3];

                if (pa == 0) { result[i] = 0; continue; }
                byte ub = (byte)((pb * 255 + pa / 2) / pa); 
                byte ug = (byte)((pg * 255 + pa / 2) / pa);
                byte ur = (byte)((pr * 255 + pa / 2) / pa);
                result[i] = ((uint)pa << 24) | ((uint)ur << 16) | ((uint)ug << 8) | ub;
            }
            return result;
        }
    }
    private MemoryMappedFile?         _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private long _fileLength;
    public  long FileLength => _fileLength;
    private long _currentScrollLine = 0;
    private long _selectionStart    = -1;
    private long _selectionEnd      = -1;
    private bool HasSelection  => _selectionStart != -1 && _selectionEnd != -1;
    private long SelectionMin  => Math.Min(_selectionStart, _selectionEnd);
    private long SelectionMax  => Math.Max(_selectionStart, _selectionEnd);
    private long _selectedOffset = -1;
    private int  _bytesPerLine   = 24;
    private double _lineHeight   = 18;
    private double _charWidth    = 9;
    private double _fontSize     = 13;
    private double _pixelsPerDip = 1.0;

    private int CellW => (int)Math.Ceiling(_charWidth    * _pixelsPerDip);
    private int CellH => (int)Math.Ceiling(_lineHeight   * _pixelsPerDip);
    private WriteableBitmap? _bitmap;
    private uint[]           _backBuffer   = Array.Empty<uint>(); 
    private int              _bitmapWidth  = 0;
    private int              _bitmapHeight = 0;
    private bool _fullRedrawNeeded = true;
    private readonly HashSet<long> _dirtyLines = new(); 
    private GlyphCache? _glyphCache;
    private char[] _asciiLookupTable = new char[256];
    private int    _currentCodePage  = 1251;
    private byte[] _lineBuffer = new byte[256];
    private readonly object        _modLock          = new();
    private HashSet<long>          _modifiedOffsets  = new();
    private volatile HashSet<long> _modifiedSnapshot = new();
    private uint _colBackground;
    private uint _colOffset;
    private uint _colByteActive;
    private uint _colByteNull;
    private uint _colByteSelected;
    private uint _colAsciiPrintable;
    private uint _colAsciiNonPrint;
    private uint _colAsciiExt;
    private uint _colColumnHeader;
    private uint _colSelectionBg;
    private uint _colModifiedBg;
    private readonly Image _image = new() { Stretch = Stretch.None };
    public static bool IsMadnessMode { get; set; } = false;
    public bool   IsMediaMode { get; set; } = false;
    private byte[]? _mediaBuffer;
    private readonly string _videoRamp = " .:-=+*#%@";
    public Brush CurrentColor { get; set; } = Brushes.Green;
    public static readonly DependencyProperty RegionsProperty =
        DependencyProperty.Register(nameof(Regions), typeof(List<DataRegion>), typeof(VirtualizedHexView),
            new PropertyMetadata(new List<DataRegion>()));

    public static readonly DependencyProperty SelectedOffsetProperty =
        DependencyProperty.Register(nameof(SelectedOffset), typeof(long), typeof(VirtualizedHexView),
            new PropertyMetadata(-1L, OnSelectedOffsetChanged));

    public List<DataRegion> Regions
    {
        get => (List<DataRegion>)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value); 
    }

    public long SelectedOffset
    {
        get => (long)GetValue(SelectedOffsetProperty);
        set => SetValue(SelectedOffsetProperty, value);
    }

    public event EventHandler<long>? OffsetSelected;
    public VirtualizedHexView()
    {
        InitializeAsciiTable(28591);
        ClipToBounds = true;
        Focusable    = true;
        AddVisualChild(_image);
        AddLogicalChild(_image);

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RefreshColorCache();
            RebuildGlyphCache();
            RequestFullRedraw();
        };

        SizeChanged += (_, _) =>
        {
            ResizeBitmap((int)ActualWidth, (int)ActualHeight);
            RequestFullRedraw();
        };
    }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _image;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width)  ? 1000 : available.Width;
        double h = double.IsInfinity(available.Height) ? 800  : available.Height;
        _image.Measure(new Size(w, h));
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _image.Arrange(new Rect(finalSize));
        return finalSize;
    }
    public void RefreshColorCache()
    {
        _colBackground    = ColorToArgb(ThemeColor("Hex_Background",       Color.FromRgb( 30,  30,  30)));
        _colOffset        = ColorToArgb(ThemeColor("HexOffset",             Color.FromRgb(160, 160, 160)));
        _colByteActive    = ColorToArgb(ThemeColor("Hex_ByteActive",        Color.FromRgb(173, 216, 230)));
        _colByteNull      = ColorToArgb(ThemeColor("Hex_ByteNull",          Color.FromRgb( 80,  80,  80)));
        _colByteSelected  = ColorToArgb(ThemeColor("Hex_ByteSelected",      Color.FromRgb(255, 255,   0)));
        _colAsciiPrintable= ColorToArgb(ThemeColor("Hex_AsciiPrintable",    Color.FromRgb(144, 238, 144)));
        _colAsciiNonPrint = ColorToArgb(ThemeColor("Hex_AsciiNonPrintable", Color.FromRgb(100, 100, 100)));
        _colColumnHeader  = ColorToArgb(ThemeColor("ForegroundSecondary",   Color.FromRgb(100, 100, 100)));
        _colAsciiExt      = ColorToArgb(Color.FromRgb( 60, 120,  60));
        _colSelectionBg   = ColorToArgb(Color.FromArgb(100,  51, 153, 255));
        _colModifiedBg    = ColorToArgb(Color.FromArgb( 80, 255,   0, 128));

        RebuildGlyphCache();
        RequestFullRedraw();
    }

    private static uint ColorToArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Color ThemeColor(string key, Color fallback)
    {
        var res = Application.Current?.TryFindResource(key);
        if (res is SolidColorBrush scb) return scb.Color;
        if (res is Color c) return c;
        return fallback;
    }
    private void RebuildGlyphCache()
    {
        if (_pixelsPerDip == 0) return;

        _glyphCache?.Clear();
        _glyphCache = new GlyphCache(
            GetGlyphTypeface(), _fontSize,
            CellW, CellH, _pixelsPerDip);
        Task.Run(WarmupGlyphCache);
    }

    private GlyphTypeface GetGlyphTypeface()
    {
        var tf = new Typeface(new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        tf.TryGetGlyphTypeface(out var gtf);
        return gtf;
    }

    private void WarmupGlyphCache()
    {
        if (_glyphCache == null) return;
        uint[] colors = {
            _colByteActive, _colByteNull, _colByteSelected,
            _colAsciiPrintable, _colAsciiNonPrint, _colAsciiExt,
            _colOffset, _colColumnHeader
        };
        char[] hexChars = "0123456789ABCDEF ".ToCharArray();
        foreach (var col in colors)
            foreach (var c in hexChars)
                _glyphCache.Get(c, col);
        foreach (var col in colors)
            for (int i = 0; i < 256; i++)
                _glyphCache.Get(_asciiLookupTable[i], col);
        Dispatcher.BeginInvoke(() => RequestFullRedraw());
    }
    private void ResizeBitmap(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (w == _bitmapWidth && h == _bitmapHeight) return;

        _bitmapWidth  = w;
        _bitmapHeight = h;

        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _backBuffer = new uint[w * h]; 
        _image.Source = _bitmap;
        _fullRedrawNeeded = true;
    }
    private void RequestFullRedraw()
    {
        _fullRedrawNeeded = true;
        InvalidateVisual();
    }
    private void MarkLineDirty(long offset)
    {
        _dirtyLines.Add(offset / _bytesPerLine);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap == null || _bitmapWidth == 0)
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                null, new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }

        if (_fileLength == 0 || _accessor == null)
        {
            FillBackground(_backBuffer, _bitmapWidth, _bitmapHeight, _colBackground);
            DrawStringToBuffer("No file loaded. Drag & drop PE file here.",
                _bitmapWidth / 2 - 150, _bitmapHeight / 2, _colColumnHeader);
            FlushBitmapFull();
            return;
        }

        if (_glyphCache == null) return;

        if (_fullRedrawNeeded)
        {
            RenderFullFrame();
            FlushBitmapFull();
            _fullRedrawNeeded = false;
            _dirtyLines.Clear();
        }
        else if (_dirtyLines.Count > 0)
        {
            foreach (long lineIdx in _dirtyLines)
                RenderLine(lineIdx);
            FlushBitmapDirty();
            _dirtyLines.Clear();
        }
    }
    private void RenderFullFrame()
    {
        FillBackground(_backBuffer, _bitmapWidth, _bitmapHeight, _colBackground);
        int offsetColPx    = (int)(120 * _pixelsPerDip);
        int hexColPx       = (int)(_bytesPerLine * 3 * _charWidth * _pixelsPerDip);
        int asciiColStartPx= offsetColPx + hexColPx + (int)(20 * _pixelsPerDip);

        DrawStringToBuffer("Offset",   10,               5, _colColumnHeader);
        DrawStringToBuffer("Hex View", offsetColPx + 10, 5, _colColumnHeader);
        DrawStringToBuffer("ASCII",    asciiColStartPx,  5, _colColumnHeader);
        long totalLines  = (_fileLength + _bytesPerLine - 1) / _bytesPerLine;
        int  visibleLines= (int)(ActualHeight / _lineHeight) + 2;
        long firstLine   = _currentScrollLine;
        long lastLine    = Math.Min(firstLine + visibleLines, totalLines);

        var modSnap = _modifiedSnapshot;

        for (long line = firstLine; line < lastLine; line++)
        {
            long offset = line * _bytesPerLine;
            if (offset >= _fileLength) break;
            RenderLineInternal(line, offset, offsetColPx, asciiColStartPx, modSnap);
        }
    }
    private void RenderLine(long lineIdx)
    {
        long offset = lineIdx * _bytesPerLine;
        if (offset >= _fileLength) return;

        int offsetColPx    = (int)(120 * _pixelsPerDip);
        int hexColPx       = (int)(_bytesPerLine * 3 * _charWidth * _pixelsPerDip);
        int asciiColStartPx= offsetColPx + hexColPx + (int)(20 * _pixelsPerDip);
        int yPx = LineToPixelY(lineIdx);
        FillRect(_backBuffer, _bitmapWidth, 0, yPx, _bitmapWidth, CellH, _colBackground);

        RenderLineInternal(lineIdx, offset, offsetColPx, asciiColStartPx, _modifiedSnapshot);
    }

    private void RenderLineInternal(long lineIdx, long offset,
        int offsetColPx, int asciiColStartPx, HashSet<long> modSnap)
    {
        int yPx = LineToPixelY(lineIdx);
        if (yPx + CellH > _bitmapHeight) return;
        DrawStringToBuffer($"{offset:X8}", 10, yPx, _colOffset);
        int bytesToDraw = (int)Math.Min(_bytesPerLine, _fileLength - offset);
        if (IsMediaMode && _mediaBuffer != null)
        {
            int startIdx = (int)(offset - _currentScrollLine * _bytesPerLine);
            for (int i = 0; i < bytesToDraw; i++)
            {
                int bufIdx = startIdx + i;
                _lineBuffer[i] = (bufIdx >= 0 && bufIdx < _mediaBuffer.Length)
                    ? _mediaBuffer[bufIdx] : (byte)0;
            }
        }
        else
        {
            _accessor!.ReadArray(offset, _lineBuffer, 0, bytesToDraw);
        }

        bool hasSelection = HasSelection;
        long selMin = hasSelection ? SelectionMin : -1;
        long selMax = hasSelection ? SelectionMax : -1;
        int hexCellStepPx  = (int)Math.Round(3 * _charWidth * _pixelsPerDip);
        int hexCellWidthPx = (int)Math.Round(_charWidth * 2.0 * _pixelsPerDip);
        for (int i = 0; i < bytesToDraw; i++)
        {
            long byteOffset = offset + i;
            byte value      = _lineBuffer[i];
            int  xPx        = offsetColPx + 10 + i * hexCellStepPx;
            if (hasSelection && byteOffset >= selMin && byteOffset <= selMax)
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx,
                    hexCellWidthPx, CellH - 2, _colSelectionBg);
            if (modSnap.Contains(byteOffset))
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx,
                    hexCellWidthPx, CellH - 2, _colModifiedBg);
            uint hexColor = (byteOffset == _selectedOffset) ? _colByteSelected
                          : (value == 0x00)                 ? _colByteNull
                                                            : _colByteActive;
            char hi = HexChar(value >> 4);
            char lo = HexChar(value & 0xF);
            BlitGlyph(hi, xPx,          yPx, hexColor);
            BlitGlyph(lo, xPx + CellW,  yPx, hexColor);
            if (byteOffset == _selectedOffset)
                DrawRect(_backBuffer, _bitmapWidth,
                    xPx - 1, yPx - 1, hexCellWidthPx + 2, CellH, _colByteSelected);
        }
        for (int i = 0; i < bytesToDraw; i++)
        {
            long byteOffset = offset + i;
            byte value      = _lineBuffer[i];
            int  xPx        = asciiColStartPx + i * CellW;

            if (hasSelection && byteOffset >= selMin && byteOffset <= selMax)
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx, CellW, CellH - 2, _colSelectionBg);

            char displayChar;
            uint asciiColor;

            if (IsMediaMode)
            {
                int rampIdx = value * (_videoRamp.Length - 1) / 255;
                displayChar = _videoRamp[rampIdx];
                asciiColor  = _colAsciiPrintable;
            }
            else
            {
                displayChar = _asciiLookupTable[value];
                asciiColor  = (value >= 32 && value <= 126) ? _colAsciiPrintable
                            : (value > 127 && displayChar != '.') ? _colAsciiExt
                            : _colAsciiNonPrint;
            }

            BlitGlyph(displayChar, xPx, yPx, asciiColor);
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BlitGlyph(char c, int dstX, int dstY, uint colorArgb)
    {
        if (_glyphCache == null) return;
        var glyph = _glyphCache.Get(c, colorArgb);

        int cw = _glyphCache.CellW;
        int ch = _glyphCache.CellH;
        int srcX0 = 0, srcY0 = 0;
        int drawW = cw, drawH = ch;
        if (dstX < 0) { srcX0 -= dstX; drawW += dstX; dstX = 0; }
        if (dstY < 0) { srcY0 -= dstY; drawH += dstY; dstY = 0; }
        if (dstX + drawW > _bitmapWidth)  drawW = _bitmapWidth  - dstX;
        if (dstY + drawH > _bitmapHeight) drawH = _bitmapHeight - dstY;
        if (drawW <= 0 || drawH <= 0) return;

        for (int row = 0; row < drawH; row++)
        {
            int dstRowStart = (dstY + row) * _bitmapWidth + dstX;
            int srcRowStart = (srcY0 + row) * cw + srcX0;

            for (int col = 0; col < drawW; col++)
            {
                uint srcPixel = glyph[srcRowStart + col];
                byte srcA = (byte)(srcPixel >> 24);
                if (srcA == 0) continue;
                if (srcA == 255)
                {
                    _backBuffer[dstRowStart + col] = srcPixel;
                    continue;
                }
                uint dstPixel = _backBuffer[dstRowStart + col];
                byte dstR = (byte)(dstPixel >> 16);
                byte dstG = (byte)(dstPixel >> 8);
                byte dstB = (byte)(dstPixel);
                byte srcR = (byte)(srcPixel >> 16);
                byte srcG = (byte)(srcPixel >> 8);
                byte srcB = (byte)(srcPixel);
                int invA = 255 - srcA;

                _backBuffer[dstRowStart + col] =
                    0xFF000000u |
                    (uint)((srcR * srcA + dstR * invA) / 255) << 16 |
                    (uint)((srcG * srcA + dstG * invA) / 255) << 8  |
                    (uint)((srcB * srcA + dstB * invA) / 255);
            }
        }
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

        byte srcR = (byte)(colorArgb >> 16);
        byte srcG = (byte)(colorArgb >> 8);
        byte srcB = (byte)(colorArgb);

        if (srcA == 255)
        {
            uint solid = 0xFF000000u | ((uint)srcR << 16) | ((uint)srcG << 8) | srcB;
            for (int row = y; row < y2; row++)
            {
                int rowStart = row * stride;
                for (int col = x; col < x2; col++)
                    buf[rowStart + col] = solid;
            }
        }
        else
        {
            int invA = 255 - srcA;
            for (int row = y; row < y2; row++)
            {
                int rowStart = row * stride;
                for (int col = x; col < x2; col++)
                {
                    uint dst = buf[rowStart + col];
                    byte dstR = (byte)(dst >> 16);
                    byte dstG = (byte)(dst >> 8);
                    byte dstB = (byte)(dst);
                    buf[rowStart + col] =
                        0xFF000000u |
                        (uint)((srcR * srcA + dstR * invA) / 255) << 16 |
                        (uint)((srcG * srcA + dstG * invA) / 255) << 8  |
                        (uint)((srcB * srcA + dstB * invA) / 255);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillBackground(uint[] buf, int w, int h, uint color)
    {
        uint solid = color | 0xFF000000u;
        buf.AsSpan(0, w * h).Fill(solid);
    }
    private static void DrawRect(uint[] buf, int stride,
        int x, int y, int w, int h, uint color)
    {
        uint solid = color | 0xFF000000u;
        for (int i = x; i < x + w; i++)
        {
            if (y >= 0 && y * stride + i < buf.Length)         buf[y * stride + i] = solid;
            if ((y+h-1) * stride + i < buf.Length)             buf[(y+h-1) * stride + i] = solid;
        }
        for (int j = y; j < y + h; j++)
        {
            if (j * stride + x < buf.Length)                   buf[j * stride + x] = solid;
            if (j * stride + x + w - 1 < buf.Length)           buf[j * stride + x + w - 1] = solid;
        }
    }
    private unsafe void FlushBitmapFull()
    {
        if (_bitmap == null) return;
        _bitmap.Lock();
        try
        {
            fixed (uint* src = _backBuffer)
            {
                Buffer.MemoryCopy(
                    src,
                    (void*)_bitmap.BackBuffer,
                    _bitmapWidth * _bitmapHeight * 4,
                    _bitmapWidth * _bitmapHeight * 4);
            }
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmapWidth, _bitmapHeight));
        }
        finally { _bitmap.Unlock(); }
    }

    private unsafe void FlushBitmapDirty()
    {
        if (_bitmap == null) return;
        _bitmap.Lock();
        try
        {
            int stride = _bitmapWidth * 4;
            fixed (uint* src = _backBuffer)
            {
                foreach (long lineIdx in _dirtyLines)
                {
                    int yPx = LineToPixelY(lineIdx);
                    if (yPx < 0 || yPx + CellH > _bitmapHeight) continue;
                    byte* dstPtr = (byte*)_bitmap.BackBuffer + yPx * stride;
                    byte* srcPtr = (byte*)(src + yPx * _bitmapWidth);
                    Buffer.MemoryCopy(srcPtr, dstPtr, CellH * stride, CellH * stride);
                    _bitmap.AddDirtyRect(new Int32Rect(0, yPx, _bitmapWidth, CellH));
                }
            }
        }
        finally { _bitmap.Unlock(); }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LineToPixelY(long lineIdx) =>
        (int)((lineIdx - _currentScrollLine) * _lineHeight * _pixelsPerDip) + (int)(25 * _pixelsPerDip);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char HexChar(int nibble) =>
        (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    private void DrawStringToBuffer(string text, int xPx, int yPx, uint color)
    {
        if (_glyphCache == null) return;
        for (int i = 0; i < text.Length; i++)
            BlitGlyph(text[i], xPx + i * CellW, yPx, color);
    }
    public void RefreshBrushCache() => RefreshColorCache(); 

    public void WriteByte(long offset, byte value)
    {
        if (_accessor == null) return;
        _accessor.Write(offset, value);
        lock (_modLock)
        {
            var newSet = new HashSet<long>(_modifiedOffsets) { offset };
            _modifiedOffsets  = newSet;
            _modifiedSnapshot = newSet;
        }
        MarkLineDirty(offset); 
    }

    public void LoadFile(string filePath)
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
        if (!File.Exists(filePath)) return;
        _fileLength = new FileInfo(filePath).Length;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        _currentScrollLine = 0;
        RequestFullRedraw();
    }

    public void Dispose() { _accessor?.Dispose(); _mmf?.Dispose(); }
    public void Save()    => _accessor?.Flush();

    public byte ReadByte(long offset)
    {
        if (_accessor == null || offset < 0 || offset >= _fileLength) return 0;
        return _accessor.ReadByte(offset);
    }

    public void ReadBytes(long offset, byte[] buffer)
    {
        if (_accessor == null || offset < 0 || offset >= _fileLength) return;
        int count = (int)Math.Min(buffer.Length, _fileLength - offset);
        if (count > 0) _accessor.ReadArray(offset, buffer, 0, count);
    }

    public void ReadBytes(long offset, Span<byte> buffer)
    {
        if (_accessor == null || offset < 0 || offset >= _fileLength) return;
        int count = (int)Math.Min(buffer.Length, _fileLength - offset);
        if (count <= 0) return;
        byte[] tmp = ArrayPool<byte>.Shared.Rent(count);
        try { _accessor.ReadArray(offset, tmp, 0, count); tmp.AsSpan(0, count).CopyTo(buffer); }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
    }

    public void ScrollToOffset(long offset)
    {
        if (offset < 0 || offset >= _fileLength) return;
        _currentScrollLine = offset / _bytesPerLine;
        RequestFullRedraw();
    }

    public void SetMediaFrame(byte[] frame) { _mediaBuffer = frame; RequestFullRedraw(); }

    public void ChangeEncoding(int codePage)
    {
        InitializeAsciiTable(codePage);
        RebuildGlyphCache(); 
        RequestFullRedraw();
    }

    public void JumpToNextChange()
    {
        var snap = _modifiedSnapshot;
        if (snap.Count == 0) return;
        long startFrom = _selectedOffset;
        long? next = null; long bestDist = long.MaxValue;
        foreach (long o in snap)
            if (o > startFrom && o - startFrom < bestDist) { bestDist = o - startFrom; next = o; }
        if (next == null)
        {
            long minVal = long.MaxValue;
            foreach (long o in snap) if (o < minVal) minVal = o;
            if (minVal != long.MaxValue) next = minVal;
        }
        if (next.HasValue)
        {
            _selectedOffset = next.Value;
            _currentScrollLine = Math.Max(0, _selectedOffset / _bytesPerLine - 2);
            OffsetSelected?.Invoke(this, _selectedOffset);
            RequestFullRedraw();
        }
    }

    public void InitializeAsciiTable(int codePage)
    {
        _currentCodePage = codePage;
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var enc = System.Text.Encoding.GetEncoding(codePage);
            byte[] all = new byte[256];
            for (int i = 0; i < 256; i++) all[i] = (byte)i;
            string decoded = enc.GetString(all);
            for (int i = 0; i < 256; i++)
            {
                if      (i < 32 || i == 127) _asciiLookupTable[i] = '.';
                else if (i < 127)            _asciiLookupTable[i] = (char)i;
                else { char c = decoded[i];  _asciiLookupTable[i] = char.IsControl(c) ? '.' : c; }
            }
        }
        catch { for (int i = 0; i < 256; i++) _asciiLookupTable[i] = i >= 32 && i <= 126 ? (char)i : '.'; }
    }
    private static void OnSelectedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VirtualizedHexView v)
        { v._selectedOffset = (long)e.NewValue; v.ScrollToOffset(v._selectedOffset); v.RequestFullRedraw(); }
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        long clicked = HitTest(e.GetPosition(this));
        if (clicked < 0 || clicked >= _fileLength) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selectionStart != -1)
            _selectionEnd = clicked;
        else { _selectionStart = clicked; _selectionEnd = clicked; _selectedOffset = clicked; }
        OffsetSelected?.Invoke(this, _selectedOffset);
        RequestFullRedraw();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e); e.Handled = true;
        int mult = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 100
                 : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)   ? 1000 : 1;
        long maxLines = _fileLength / _bytesPerLine;
        _currentScrollLine = Math.Clamp(_currentScrollLine + (e.Delta > 0 ? -3 : 3) * mult, 0, maxLines);
        RequestFullRedraw();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var action = HotkeyManager.GetAction(Keyboard.Modifiers, e.Key);
        if (action == EUVAAction.CopyHex)         { CopyAsHex();       e.Handled = true; }
        else if (action == EUVAAction.CopyCArray)  { CopyAsCArray();   e.Handled = true; }
        else if (action == EUVAAction.CopyPlainText){ CopyAsPlainText();e.Handled = true; }
        if (e.Key == Key.F3) { JumpToNextChange(); e.Handled = true; }
    }

    private long HitTest(Point pos)
    {
        double offsetColW    = 120;
        double hexColW       = _bytesPerLine * 3 * _charWidth;
        double asciiColStart = offsetColW + hexColW + 20;
        long lineIndex = (long)((pos.Y - 25) / _lineHeight) + _currentScrollLine;
        if (lineIndex < 0) return -1;
        long baseOffset = lineIndex * _bytesPerLine;
        int byteIndex = -1;
        if (pos.X >= offsetColW + 10 && pos.X < asciiColStart - 10)
            byteIndex = (int)((pos.X - offsetColW - 10) / (3 * _charWidth));
        else if (pos.X >= asciiColStart)
            byteIndex = (int)((pos.X - asciiColStart) / _charWidth);
        if (byteIndex < 0 || byteIndex >= _bytesPerLine) return -1;
        long final = baseOffset + byteIndex;
        return (final >= 0 && final < _fileLength) ? final : -1;
    }
    private void CopyAsHex()
    {
        if (!HasSelection) return;
        long count = Math.Min(SelectionMax - SelectionMin + 1, 10 * 1024 * 1024);
        var bytes = new byte[count]; ReadBytes(SelectionMin, bytes);
        Clipboard.SetText(BitConverter.ToString(bytes).Replace("-", " "));
    }

    private void CopyAsCArray()
    {
        if (!HasSelection) return;
        long count = Math.Min(SelectionMax - SelectionMin + 1, 1024 * 1024);
        var bytes = new byte[count]; ReadBytes(SelectionMin, bytes);
        var sb = new System.Text.StringBuilder((int)count * 6);
        sb.Append("byte[] data = { ");
        for (int i = 0; i < bytes.Length; i++) { sb.Append($"0x{bytes[i]:X2}"); if (i < bytes.Length - 1) sb.Append(", "); }
        sb.Append(" };"); Clipboard.SetText(sb.ToString());
    }

    private void CopyAsPlainText()
    {
        if (!HasSelection) return;
        long count = Math.Min(SelectionMax - SelectionMin + 1, 10 * 1024 * 1024);
        var bytes = new byte[count]; ReadBytes(SelectionMin, bytes);
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) chars[i] = _asciiLookupTable[bytes[i]];
        Clipboard.SetText(new string(chars));
    }

}