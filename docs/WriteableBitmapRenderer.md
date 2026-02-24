## WriteableBitmap Render

Details on the implementation of WriteableBitmap
An array of pixels is created, and the same letters or other objects on the scene are manually drawn into this array. After this, the array is copied to video memory in a single step. I used this approach to ensure extreme performance and move away as much as possible from the abstract nature of WPF. This method offers greater efficiency because we don't have to redraw the entire screen if something changes or use abstract objects, as WPF prefers, which results in a glitchy interface.

---

**VirtualizedHexView.cs**


```csharp
        public VirtualizedHexView()
    {
        if (PresentationSource.FromVisual(this) is { } ps)
            _pixelsPerDip = ps.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        InitializeAsciiTable(28591);
        ClipToBounds = true;
        Focusable = true;
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
        double w = double.IsInfinity(available.Width) ? 1000 : available.Width;
        double h = double.IsInfinity(available.Height) ? 800 : available.Height;
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
        _colBackground = ColorToArgb(ThemeColor("Hex_Background", Color.FromRgb(30, 30, 30)));
        _colOffset = ColorToArgb(ThemeColor("HexOffset", Color.FromRgb(160, 160, 160)));
        _colByteActive = ColorToArgb(ThemeColor("Hex_ByteActive", Color.FromRgb(173, 216, 230)));
        _colByteNull = ColorToArgb(ThemeColor("Hex_ByteNull", Color.FromRgb(80, 80, 80)));
        _colByteSelected = ColorToArgb(ThemeColor("Hex_ByteSelected", Color.FromRgb(255, 255, 0)));
        _colAsciiPrintable = ColorToArgb(ThemeColor("Hex_AsciiPrintable", Color.FromRgb(144, 238, 144)));
        _colAsciiNonPrint = ColorToArgb(ThemeColor("Hex_AsciiNonPrintable", Color.FromRgb(100, 100, 100)));
        _colColumnHeader = ColorToArgb(ThemeColor("ForegroundSecondary", Color.FromRgb(100, 100, 100)));
        _colAsciiExt = ColorToArgb(Color.FromRgb(60, 120, 60));
        _colSelectionBg = ColorToArgb(Color.FromArgb(100, 51, 153, 255));
        _colModifiedBg = ColorToArgb(Color.FromArgb(80, 255, 0, 128));
        _colYaraHit = ColorToArgb(Color.FromArgb(100, 255, 255, 0));

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
        var cache = _glyphCache;
        if (cache == null) return;
        uint[] colors = {
            _colByteActive, _colByteNull, _colByteSelected,
            _colAsciiPrintable, _colAsciiNonPrint, _colAsciiExt,
            _colOffset, _colColumnHeader
        };
        char[] hexChars = "0123456789ABCDEF ".ToCharArray();
        foreach (var col in colors)
            foreach (var c in hexChars)
                cache.Get(c, col);
        foreach (var col in colors)
            for (int i = 0; i < 256; i++)
                cache.Get(_asciiLookupTable[i], col);
        Dispatcher.BeginInvoke(() => RequestFullRedraw());
    }
    private void ResizeBitmap(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (w == _bitmapWidth && h == _bitmapHeight) return;
        _bitmapWidth = w;
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
            TakeModifiedSnapshot();
            RenderFullFrame();
            FlushBitmapFull();
            _fullRedrawNeeded = false;
            _dirtyLines.Clear();
        }
        else if (_dirtyLines.Count > 0)
        {
            TakeModifiedSnapshot();
            foreach (long lineIdx in _dirtyLines)
                RenderLine(lineIdx);
            FlushBitmapDirty();
            _dirtyLines.Clear();
        }
    }
    private void TakeModifiedSnapshot()
    {
        HashSet<long> snap;
        lock (_modLock)
            snap = new HashSet<long>(_modifiedOffsets);
        _modifiedSnapshot = snap;
    }
    private void RenderFullFrame()
    {
        FillBackground(_backBuffer, _bitmapWidth, _bitmapHeight, _colBackground);
        int offsetColPx = (int)(120 * _pixelsPerDip);
        int hexColPx = (int)(_bytesPerLine * 3 * _charWidth * _pixelsPerDip);
        int asciiColStartPx = offsetColPx + hexColPx + (int)(20 * _pixelsPerDip);

        DrawStringToBuffer("Offset", 10, 5, _colColumnHeader);
        DrawStringToBuffer("Hex View", offsetColPx + 10, 5, _colColumnHeader);
        DrawStringToBuffer("ASCII", asciiColStartPx, 5, _colColumnHeader);

        long totalLines = (_fileLength + _bytesPerLine - 1) / _bytesPerLine;
        int visibleLines = (int)(ActualHeight / _lineHeight) + 2;
        long firstLine = _currentScrollLine;
        long lastLine = Math.Min(firstLine + visibleLines, totalLines);
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

        int offsetColPx = (int)(120 * _pixelsPerDip);
        int hexColPx = (int)(_bytesPerLine * 3 * _charWidth * _pixelsPerDip);
        int asciiColStartPx = offsetColPx + hexColPx + (int)(20 * _pixelsPerDip);
        int yPx = LineToPixelY(lineIdx);
        FillRect(_backBuffer, _bitmapWidth, 0, yPx, _bitmapWidth, CellH, _colBackground);
        RenderLineInternal(lineIdx, offset, offsetColPx, asciiColStartPx, _modifiedSnapshot);
    }

    private void RenderLineInternal(long lineIdx, long offset,
        int offsetColPx, int asciiColStartPx, HashSet<long> modSnap)
    {
        long yPxLong = (lineIdx - _currentScrollLine) * (long)(_lineHeight * _pixelsPerDip)
                       + (long)(25 * _pixelsPerDip);
        if (yPxLong < 0 || yPxLong > int.MaxValue) return;
        int yPx = (int)yPxLong;
        if (yPx + CellH > _bitmapHeight) return;

        DrawStringToBuffer($"{offset:X8}", 10, yPx, _colOffset);
        int bytesToDraw = (int)Math.Min(_bytesPerLine, _fileLength - offset);

        if (IsMediaMode && _mediaBuffer != null)
        {
            for (int i = 0; i < bytesToDraw; i++)
            {
                long bufIdx = offset + i;
                _lineBuffer[i] = (bufIdx >= 0 && bufIdx < _mediaBuffer.Length)
                    ? _mediaBuffer[bufIdx] : (byte)0;
            }
        }
        else
        {
            _accessorLock.EnterReadLock();
            try
            {
                if (_accessor != null)
                    _accessor.ReadArray(offset, _lineBuffer, 0, bytesToDraw);
            }
            finally { _accessorLock.ExitReadLock(); }
        }

        bool hasSelection = HasSelection;
        long selMin = hasSelection ? SelectionMin : -1;
        long selMax = hasSelection ? SelectionMax : -1;
        int hexCellStepPx = (int)Math.Round(3 * _charWidth * _pixelsPerDip);
        int hexCellWidthPx = (int)Math.Round(_charWidth * 2.0 * _pixelsPerDip);

        for (int i = 0; i < bytesToDraw; i++)
        {
            long byteOffset = offset + i;
            byte value = _lineBuffer[i];
            int xPx = offsetColPx + 10 + i * hexCellStepPx;

            if (hasSelection && byteOffset >= selMin && byteOffset <= selMax)
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx,
                    hexCellWidthPx, CellH - 2, _colSelectionBg);

            if (modSnap.Contains(byteOffset))
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx,
                    hexCellWidthPx, CellH - 2, _colModifiedBg);

            if (_yaraSnapshot.Contains(byteOffset))
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx,
                    hexCellWidthPx, CellH - 2, _colYaraHit);

            uint hexColor = (byteOffset == _selectedOffset) ? _colByteSelected
                          : (value == 0x00) ? _colByteNull
                                                            : _colByteActive;
            char hi = HexChar(value >> 4);
            char lo = HexChar(value & 0xF);
            BlitGlyph(hi, xPx, yPx, hexColor);
            BlitGlyph(lo, xPx + CellW, yPx, hexColor);

            if (byteOffset == _selectedOffset)
                DrawRect(_backBuffer, _bitmapWidth,
                    xPx - 1, yPx - 1, hexCellWidthPx + 2, CellH, _colByteSelected);
        }

        for (int i = 0; i < bytesToDraw; i++)
        {
            long byteOffset = offset + i;
            byte value = _lineBuffer[i];
            int xPx = asciiColStartPx + i * CellW;

            if (hasSelection && byteOffset >= selMin && byteOffset <= selMax)
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx, CellW, CellH - 2, _colSelectionBg);

            if (_yaraSnapshot.Contains(byteOffset))
                FillRect(_backBuffer, _bitmapWidth, xPx, yPx, CellW, CellH - 2, _colYaraHit);

            char displayChar;
            uint asciiColor;

            if (IsMediaMode)
            {
                int rampIdx = value * (_videoRamp.Length - 1) / 255;
                displayChar = _videoRamp[rampIdx];
                asciiColor = _colAsciiPrintable;
            }
            else
            {
                displayChar = _asciiLookupTable[value];
                asciiColor = (value >= 32 && value <= 126) ? _colAsciiPrintable
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
        if (dstX + drawW > _bitmapWidth) drawW = _bitmapWidth - dstX;
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
                int invA = 255 - srcA;

                byte outR = (byte)(((srcPixel >> 16) & 0xFF)
                            + (((dstPixel >> 16) & 0xFF) * invA + 127 >> 8));
                byte outG = (byte)(((srcPixel >> 8) & 0xFF)
                            + (((dstPixel >> 8) & 0xFF) * invA + 127 >> 8));
                byte outB = (byte)((srcPixel & 0xFF)
                            + ((dstPixel & 0xFF) * invA + 127 >> 8));

                _backBuffer[dstRowStart + col] =
                    0xFF000000u | ((uint)outR << 16) | ((uint)outG << 8) | outB;
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
                buf.AsSpan(rowStart + x, x2 - x).Fill(solid);
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
                    buf[rowStart + col] =
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
        uint solid = color | 0xFF000000u;
        buf.AsSpan(0, w * h).Fill(solid);
    }

    private static void DrawRect(uint[] buf, int stride,
        int x, int y, int w, int h, uint color)
    {
        uint solid = color | 0xFF000000u;
        for (int i = x; i < x + w; i++)
        {
            if (y >= 0 && y * stride + i < buf.Length) buf[y * stride + i] = solid;
            if ((y + h - 1) * stride + i < buf.Length) buf[(y + h - 1) * stride + i] = solid;
        }
        for (int j = y; j < y + h; j++)
        {
            if (j * stride + x < buf.Length) buf[j * stride + x] = solid;
            if (j * stride + x + w - 1 < buf.Length) buf[j * stride + x + w - 1] = solid;
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
                    _bitmapWidth * _bitmapHeight * 4L,
                    _bitmapWidth * _bitmapHeight * 4L);
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
                    long yPxLong = (lineIdx - _currentScrollLine) * (long)(_lineHeight * _pixelsPerDip)
                                   + (long)(25 * _pixelsPerDip);
                    if (yPxLong < 0 || yPxLong > int.MaxValue) continue;
                    int yPx = (int)yPxLong;
                    if (yPx < 0 || yPx + CellH > _bitmapHeight) continue;
                    byte* dstPtr = (byte*)_bitmap.BackBuffer + yPx * stride;
                    byte* srcPtr = (byte*)(src + yPx * _bitmapWidth);
                    Buffer.MemoryCopy(srcPtr, dstPtr, (long)CellH * stride, (long)CellH * stride);
                    _bitmap.AddDirtyRect(new Int32Rect(0, yPx, _bitmapWidth, CellH));
                }
            }
        }
        finally { _bitmap.Unlock(); }
    }
```