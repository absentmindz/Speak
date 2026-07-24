using System;
using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class ClipboardHistory
{
    private readonly LinkedList<string> _entries = new LinkedList<string>();
    private readonly int _maxEntries;

    public int Count => _entries.Count;

    public IReadOnlyCollection<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return new List<string>(_entries);
            }
        }
    }

    public ClipboardHistory(int maxEntries = 10)
    {
        _maxEntries = Math.Max(1, maxEntries);
    }

    public void Push(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        lock (_entries)
        {
            if (_entries.Count > 0 && _entries.First!.Value == text)
                return;

            _entries.AddFirst(text);

            while (_entries.Count > _maxEntries)
                _entries.RemoveLast();
        }
    }

    public string Peek()
    {
        lock (_entries)
        {
            return _entries.First?.Value ?? "";
        }
    }

    public string Pop()
    {
        lock (_entries)
        {
            if (_entries.First == null)
                return "";

            string value = _entries.First.Value;
            _entries.RemoveFirst();
            return value;
        }
    }

    public void Clear()
    {
        lock (_entries)
        {
            _entries.Clear();
        }
    }
}