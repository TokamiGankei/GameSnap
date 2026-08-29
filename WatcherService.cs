using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;      // ✅ 新增
using System.Threading;
using System.Threading.Tasks;

namespace GameSnapPlugin
{
    public class WatcherService : IDisposable
    {
        private readonly GameSnapSettings  _settings;
        private readonly OrganizerService  _organizer;
        private readonly GameSnapLogger    _logger;

        private FileSystemWatcher? _watcher;
        private Timer?             _pollingTimer;
        private bool               _disposed;

        // ✅ 新文件处理队列（仅用于新文件，处理完后移除）
        private readonly ConcurrentDictionary<string, DateTime> _pendingFiles
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // ✅ 已处理文件缓存（防止同一个文件被重复加入队列）
        private readonly ConcurrentDictionary<string, DateTime> _processedCache
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // ✅ 处理锁，防止并发处理同一个文件
        private readonly ConcurrentDictionary<string, object> _fileLocks
            = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public WatcherService(GameSnapSettings settings, OrganizerService organizer, GameSnapLogger logger)
        {
            _settings  = settings;
            _organizer = organizer;
            _logger    = logger;
        }

        public void Start()
        {
            if (string.IsNullOrEmpty(_settings.SourceFolder) || !Directory.Exists(_settings.SourceFolder))
            {
                _logger.Error($"Source folder not found: {_settings.SourceFolder}");
                return;
            }

            // FileSystemWatcher — 只监听新文件创建
            _watcher = new FileSystemWatcher(_settings.SourceFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFileCreated;
            _watcher.Error   += OnWatcherError;

            // ✅ 启动后台处理线程
            Task.Run(() => ProcessQueueLoop());

            // 轮询作为备用（但只检查是否有新文件，不处理已有文件）
            var interval = TimeSpan.FromSeconds(_settings.PollingIntervalSeconds);
            _pollingTimer = new Timer(_ => CheckForNewFiles(), null, interval, interval);

            _logger.Info($"Watcher started on: {_settings.SourceFolder}");
        }

        public void Stop()
        {
            _pollingTimer?.Dispose();
            _pollingTimer = null;

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }

            _logger.Info("Watcher stopped.");
        }

        // ─── 新文件处理 ──────────────────────────────────────────────

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            var path = e.FullPath;

            // ✅ 检查是否已处理过（防止重复）
            if (_processedCache.ContainsKey(path))
            {
                return;
            }

            // ✅ 检查是否已在队列中
            if (_pendingFiles.ContainsKey(path))
            {
                return;
            }

            // ✅ 加入待处理队列
            _pendingFiles.TryAdd(path, DateTime.UtcNow);
            _logger.Write(LogType.Info, $"📥 New file queued: {Path.GetFileName(path)}");
        }

        // ─── 后台处理循环 ────────────────────────────────────────────

        private async Task ProcessQueueLoop()
        {
            while (!_disposed)
            {
                try
                {
                    // 获取所有待处理文件
                    var pending = _pendingFiles.Keys.ToList();

                    foreach (var filePath in pending)
                    {
                        // 如果文件已被处理，从队列移除
                        if (_processedCache.ContainsKey(filePath))
                        {
                            _pendingFiles.TryRemove(filePath, out _);
                            continue;
                        }

                        // 检查文件是否存在
                        if (!File.Exists(filePath))
                        {
                            _pendingFiles.TryRemove(filePath, out _);
                            continue;
                        }

                        // 获取文件锁，防止并发处理
                        var lockObj = _fileLocks.GetOrAdd(filePath, new object());
                        lock (lockObj)
                        {
                            // 再次检查是否已被处理
                            if (_processedCache.ContainsKey(filePath))
                            {
                                _pendingFiles.TryRemove(filePath, out _);
                                _fileLocks.TryRemove(filePath, out _);
                                continue;
                            }

                            // 等待文件写入完成
                            var fileInfo = new FileInfo(filePath);
                            if (fileInfo.Length < 1024) // 小于1KB可能还在写入
                            {
                                // 稍后重试
                                continue;
                            }

                            // ✅ 标记为已处理（立即防止重复）
                            _processedCache.TryAdd(filePath, DateTime.UtcNow);

                            // ✅ 从待处理队列移除
                            _pendingFiles.TryRemove(filePath, out _);

                            // ✅ 仅处理这个新文件
                            ProcessSingleFile(filePath);

                            // 释放锁
                            _fileLocks.TryRemove(filePath, out _);
                        }
                    }

                    // 清理过期的缓存（超过1小时的）
                    CleanupCache();

                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Queue processing error: {ex.Message}");
                    await Task.Delay(5000);
                }
            }
        }

        // ─── 处理单个文件 ────────────────────────────────────────────

        private void ProcessSingleFile(string filePath)
        {
            try
            {
                _logger.Write(LogType.Info, $"🔄 Processing new file: {Path.GetFileName(filePath)}");

                // ✅ 调用 Organizer 处理单个文件（需要新增方法）
                _organizer.ProcessSingleFile(filePath);
            }
            catch (Exception ex)
            {
                _logger.Write(LogType.Error, $"Process single file failed: {ex.Message}");
            }
        }

        // ─── 备用轮询 ─────────────────────────────────────────────────

        private void CheckForNewFiles()
        {
            try
            {
                if (string.IsNullOrEmpty(_settings.SourceFolder) || !Directory.Exists(_settings.SourceFolder))
                    return;

                // 只检查最近5秒内创建的文件
                var cutoff = DateTime.UtcNow.AddSeconds(-5);
                foreach (var file in Directory.GetFiles(_settings.SourceFolder))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.CreationTimeUtc < cutoff)
                            continue;

                        if (!_processedCache.ContainsKey(file) && !_pendingFiles.ContainsKey(file))
                        {
                            // 发现新文件，加入队列
                            _pendingFiles.TryAdd(file, DateTime.UtcNow);
                            _logger.Write(LogType.Info, $"📥 New file found by polling: {Path.GetFileName(file)}");
                        }
                    }
                    catch { /* 忽略访问冲突 */ }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Polling check error: {ex.Message}");
            }
        }

        // ─── 缓存清理 ─────────────────────────────────────────────────

        private void CleanupCache()
        {
            var now = DateTime.UtcNow;
            var expired = TimeSpan.FromHours(1);

            foreach (var key in _processedCache.Keys)
            {
                if (_processedCache.TryGetValue(key, out var time))
                {
                    if (now - time > expired)
                    {
                        _processedCache.TryRemove(key, out _);
                    }
                }
            }

            // 清理队列中过期的文件（超过5分钟）
            foreach (var key in _pendingFiles.Keys)
            {
                if (_pendingFiles.TryGetValue(key, out var time))
                {
                    if (now - time > TimeSpan.FromMinutes(5))
                    {
                        _pendingFiles.TryRemove(key, out _);
                    }
                }
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _logger.Error($"Watcher error: {e.GetException().Message}");
            Stop();
            Thread.Sleep(5000);
            Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}