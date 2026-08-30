using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backtrack.StreamDeck;

public sealed partial class StreamDeckIpcServer
{
    public async Task<object> BuildStateSnapshotAsync()
    {
        bool obsConnected = _obs.IsConnected;
        List<object> replayBuffers = new();
        List<object> recordSources = new();
        bool isMainRecording = false;
        long recordDurationMs = 0;
        long mainRecordStartUnixMs = 0;

        bool isAnySourceRecording = false;

        if (obsConnected)
        {
            try
            {
                int preferredLen = _settings.PreferredClipLengthSeconds > 0 ? _settings.PreferredClipLengthSeconds : 60;
                var rRows = await _obs.ListReplayRowsAsync();
                foreach (var r in rRows)
                {
                    int rowLen = _settings.PreferredClipLengthSeconds > 0
                        ? _settings.PreferredClipLengthSeconds
                        : (r.LengthSeconds > 0 ? r.LengthSeconds : preferredLen);

                    replayBuffers.Add(new
                    {
                        key = r.Key,
                        label = NormalizeName(r.Label, _settings),
                        raw_label = r.Label,
                        status = r.Status,
                        length = rowLen,
                        dest = r.DestDir
                    });
                }

                var recRows = await _obs.ListRecordRowsAsync();
                foreach (var rec in recRows)
                {
                    long rowDurationMs = 0;
                    long rowStartUnixMs = 0;
                    bool isRowRecording = (rec.Status == 2);

                    if (isRowRecording)
                    {
                        isAnySourceRecording = true;
                        DateTime startedAt = _getRowActiveSinceUtc?.Invoke(rec.Key) 
                            ?? _recordRowActiveSinceUtc.GetOrAdd(rec.Key, DateTime.UtcNow);
                        _recordRowActiveSinceUtc[rec.Key] = startedAt;
                        rowDurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                        rowStartUnixMs = new DateTimeOffset(startedAt).ToUnixTimeMilliseconds();
                    }
                    else
                    {
                        _recordRowActiveSinceUtc.TryRemove(rec.Key, out _);
                    }

                    recordSources.Add(new
                    {
                        key = rec.Key,
                        label = NormalizeName(rec.Label, _settings),
                        raw_label = rec.Label,
                        source = rec.SourceName,
                        filter = rec.FilterName,
                        status = rec.Status,
                        is_recording = isRowRecording,
                        duration_ms = rowDurationMs,
                        start_time_ms = rowStartUnixMs
                    });
                }

                var status = await _obs.GetRecordStatusAsync();
                isMainRecording = status.Active;
                if (isMainRecording)
                {
                    if (status.DurationMs > 0)
                    {
                        _mainRecordActiveSinceUtc = DateTime.UtcNow.AddMilliseconds(-status.DurationMs);
                        recordDurationMs = status.DurationMs;
                    }
                    else
                    {
                        _mainRecordActiveSinceUtc ??= DateTime.UtcNow;
                        recordDurationMs = (long)(DateTime.UtcNow - _mainRecordActiveSinceUtc.Value).TotalMilliseconds;
                    }
                    mainRecordStartUnixMs = new DateTimeOffset(_mainRecordActiveSinceUtc.Value).ToUnixTimeMilliseconds();
                }
                else
                {
                    _mainRecordActiveSinceUtc = null;
                }
            }
            catch { }
        }

        bool isRecording = isMainRecording || isAnySourceRecording;

        return new
        {
            obs_connected = obsConnected,
            is_recording = isRecording,
            is_main_recording = isMainRecording,
            preferred_clip_length_seconds = _settings.PreferredClipLengthSeconds > 0 ? _settings.PreferredClipLengthSeconds : 60,
            replay_buffer_minutes = _settings.ReplayBufferMinutes > 0 ? _settings.ReplayBufferMinutes : 30,
            record_duration_ms = recordDurationMs,
            main_record_start_time_ms = mainRecordStartUnixMs,
            replay_buffers = replayBuffers,
            record_sources = recordSources
        };
    }

    public async Task BroadcastStateSnapshotAsync()
    {
        var snapshot = await BuildStateSnapshotAsync();
        await BroadcastAsync("state_snapshot", snapshot);
    }

    public async Task BroadcastAsync(string eventName, object data)
    {
        if (_clients.IsEmpty) return;

        var payload = new { @event = eventName, data };
        foreach (var kv in _clients)
        {
            if (kv.Value.IsWebSocket && kv.Value.TcpClient.Connected)
            {
                try
                {
                    await SendWebSocketJsonAsync(kv.Value.Stream, payload, CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private static async Task SendWebSocketJsonAsync(NetworkStream stream, object payload, CancellationToken ct)
    {
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] frame;

        if (raw.Length < 126)
        {
            frame = new byte[2 + raw.Length];
            frame[0] = 0x81;
            frame[1] = (byte)raw.Length;
            Array.Copy(raw, 0, frame, 2, raw.Length);
        }
        else if (raw.Length <= 65535)
        {
            frame = new byte[4 + raw.Length];
            frame[0] = 0x81;
            frame[1] = 126;
            frame[2] = (byte)((raw.Length >> 8) & 0xFF);
            frame[3] = (byte)(raw.Length & 0xFF);
            Array.Copy(raw, 0, frame, 4, raw.Length);
        }
        else
        {
            frame = new byte[10 + raw.Length];
            frame[0] = 0x81;
            frame[1] = 127;
            byte[] lenBytes = BitConverter.GetBytes((ulong)raw.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
            Array.Copy(lenBytes, 0, frame, 2, 8);
            Array.Copy(raw, 0, frame, 10, raw.Length);
        }

        await stream.WriteAsync(frame, 0, frame.Length, ct);
    }
}
