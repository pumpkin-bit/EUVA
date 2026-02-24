## DirtyTracking and Snapshots

So, about the snapshot system in general: this system is used to highlight the state of bytes, which can have several states.
Changed through the scripting language built into the program.
If Yarax finds a match in bytes, it also highlights them.
overlay highlighting on raw bytes when the user selects them with the cursor.

The highlighting is applied to the byte through several layers, filling it with the base background color.
Then comes a layer of so-called metadata to check the highlighting type, whether it was selected by the user, whether it was a Yara match, or whether the bytes were modified through a scripting language.
When the background under the byte is prepared, the symbol itself is overlaid on top. Glyphcache, which already has symbols cached, helps. From there, we can extract the pixel array of a specific symbol.
We also don't just copy pixels, but also check the transparency to prepare the background.
This implementation is quite scalable, as you can simply add an additional check before rendering the glyph and add a new layer based on that.
The user doesn't see the byte mapping process, and here we use a single pass rather than redrawing everything several times.

---

**VirtualizedHexView.cs**

```csharp
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
```