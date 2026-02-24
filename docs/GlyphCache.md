## GlyphCache


So, regarding the implementation of Glyphcache, the main idea of ​​this approach is to not try to ask the system to draw a symbol for us every time. Instead, we can store all the symbols in an array and draw them accordingly in advance. We can take ready-made symbols from the cache and insert them into the desired location on the screen. This approach will be more productive, in my opinion. There are also safety guards to avoid delays during scrolling.

---
**VirtualizedHexView.cs**


```csharp
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
```