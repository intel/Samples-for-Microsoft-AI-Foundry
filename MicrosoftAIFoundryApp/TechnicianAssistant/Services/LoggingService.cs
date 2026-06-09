using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace TechnicianAssistant.Services;

/// <summary>
/// Centralized logging service that captures console output and directs it to UI.
/// Thread-safe with circular buffer to prevent memory issues.
/// </summary>
public class LoggingService
{
    private static LoggingService? _instance;
    private static readonly object _lock = new();

    private readonly ConcurrentQueue<string> _logBuffer;
    private readonly int _maxLogLines;
    private readonly StringBuilder _currentLog;
    
    public event EventHandler<string>? LogAdded;

    public static LoggingService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new LoggingService();
                }
            }
            return _instance;
        }
    }

    private LoggingService(int maxLogLines = 500)
    {
        _maxLogLines = maxLogLines;
        _logBuffer = new ConcurrentQueue<string>();
        _currentLog = new StringBuilder();
        
        // Redirect Console.WriteLine to our logger
        Console.SetOut(new LogWriter(this));
    }

    public void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logEntry = $"[{timestamp}] {message}";
        
        _logBuffer.Enqueue(logEntry);
        
        // Maintain circular buffer - remove old entries
        while (_logBuffer.Count > _maxLogLines)
        {
            _logBuffer.TryDequeue(out _);
        }
        
        // Raise event for UI update
        LogAdded?.Invoke(this, logEntry);
    }

    public string GetFullLog()
    {
        return string.Join(Environment.NewLine, _logBuffer);
    }

    public void Clear()
    {
        while (_logBuffer.TryDequeue(out _)) { }
        LogAdded?.Invoke(this, string.Empty);
    }

    private class LogWriter : TextWriter
    {
        private readonly LoggingService _logger;
        private readonly StringBuilder _lineBuffer = new();

        public LogWriter(LoggingService logger)
        {
            _logger = logger;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                _logger.Log(_lineBuffer.ToString());
                _lineBuffer.Clear();
            }
            else if (value != '\r')
            {
                _lineBuffer.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _logger.Log(value);
            }
        }
    }
}
