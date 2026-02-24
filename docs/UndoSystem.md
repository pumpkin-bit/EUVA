## UndoSystem

Also, details on the implementation of the script's rollback system include two undo systems: Ctrl+Z (sequentially) or a full rollback (Ctrl+Shift+Z).
A single rollback works by fetching the most recent change, and when we press the rollback key, we simply write the old bytes to the addresses of the last change in a loop, and the file goes backwards.

Now, regarding a full rollback, we store numbers equivalent to this method. If the user changes 50 bytes, 50 will be pushed onto the stack. We roll back the changes using a loop that takes the number 50 and rolls back 50 steps, since the user changed 50 bytes. This way, we restore everything with a single hotkey press.

---
**MainWindow.xaml.cs**


```csharp

    private void PerformUndo()

    {

        lock (_undoStack)
        {
            if (_undoStack.Count == 0) return;

            var (offset, oldData, _) = _undoStack.Pop();

            for (int i = 0; i < oldData.Length; i++) HexView.WriteByte(offset + i, oldData[i]);

        }
        HexView.InvalidateVisual();
    }
    private void PerformFullUndo()

    {
        lock (_undoStack)
        {

            if (_transactionSteps.Count == 0) return;

            int count = _transactionSteps.Pop();

            for (int i = 0; i < count && _undoStack.Count > 0; i++)

            {
                var (offset, oldData, _) = _undoStack.Pop();

                for (int j = 0; j < oldData.Length; j++) HexView.WriteByte(offset + j, oldData[j]);
            }

        }
        HexView.InvalidateVisual();

    }
```