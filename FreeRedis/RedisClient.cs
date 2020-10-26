﻿using FreeRedis.Internal;
using FreeRedis.Internal.ObjectPool;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections;

namespace FreeRedis
{
    public partial class RedisClient : IDisposable
    {
        internal BaseAdapter Adapter { get; }
        public event EventHandler<NoticeEventArgs> Notice;

        protected RedisClient(BaseAdapter adapter)
        {
            Adapter = adapter;
        }

        /// <summary>
        /// Pooling RedisClient
        /// </summary>
        public RedisClient(ConnectionStringBuilder connectionString, params ConnectionStringBuilder[] slaveConnectionStrings)
        {
            Adapter = new PoolingAdapter(this, connectionString, slaveConnectionStrings);
        }

        /// <summary>
        /// Cluster RedisClient
        /// </summary>
        public RedisClient(ConnectionStringBuilder[] clusterConnectionStrings)
        {
            throw new NotImplementedException();
            //_adapter = new ClusterAdapter(clusterConnectionStrings);
        }

        /// <summary>
        /// Sentinel RedisClient
        /// </summary>
        public RedisClient(ConnectionStringBuilder sentinelConnectionString, string[] sentinels, bool rw_splitting)
        {
            Adapter = new SentinelAdapter(this, sentinelConnectionString, sentinels, rw_splitting);
        }

        /// <summary>
        /// Single inside RedisClient
        /// </summary>
        protected internal RedisClient(RedisClient topOwner, string host, bool ssl, TimeSpan connectTimeout, TimeSpan receiveTimeout, TimeSpan sendTimeout, Action<RedisClient> connected)
        {
            Adapter = new SingleInsideAdapter(topOwner ?? this, this, host, ssl, connectTimeout, receiveTimeout, sendTimeout, connected);
        }

        ~RedisClient() => this.Dispose();
        int _disposeCounter;
        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCounter) != 1) return;
            Adapter.Dispose();
        }

        protected void CheckUseTypeOrThrow(params UseType[] useTypes)
        {
            if (useTypes?.Contains(Adapter.UseType) == true) return;
            throw new RedisException($"RedisClient: Method cannot be used in {Adapter.UseType} mode.");
        }

        internal bool _isThrowRedisSimpleError { get; set; } = true;
        protected internal RedisException RedisSimpleError { get; private set; }
        protected internal IDisposable NoneRedisSimpleError()
        {
            var old_isThrowRedisSimpleError = _isThrowRedisSimpleError;
            _isThrowRedisSimpleError = false;
            return new TempDisposable(() =>
            {
                _isThrowRedisSimpleError = old_isThrowRedisSimpleError;
                RedisSimpleError = null;
            });
        }

        public object Call(CommandPacket cmd) => Adapter.AdapaterCall<string, object>(cmd, rt => rt.ThrowOrValue());
        protected TValue Call<TValue>(CommandPacket cmd, Func<RedisResult, TValue> parse) => Adapter.AdapaterCall<string, TValue>(cmd, parse);
        protected TValue Call<TReadTextOrStream, TValue>(CommandPacket cmd, Func<RedisResult, TValue> parse) => Adapter.AdapaterCall<TReadTextOrStream, TValue>(cmd, parse);

        internal T LogCall<T>(CommandPacket cmd, Func<T> func)
        {
            if (this.Notice == null) return func();
            Exception exception = null;
            Stopwatch sw = new Stopwatch();
            T ret = default(T);
            sw.Start();
            try
            {
                ret = func();
                return ret;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw ex;
            }
            finally
            {
                sw.Stop();
                if (exception == null && _isThrowRedisSimpleError) exception = this.RedisSimpleError;
                string log;
                if (exception != null) log = $" > {exception.Message}";
                else if (cmd.ReadResult != null)
                {
                    if (cmd.ReadResult.Value is Array array)
                    {
                        var sb = new StringBuilder().Append("\r\n[");
                        var itemindex = 0;
                        foreach (var item in array)
                        {
                            if (itemindex++ > 0) sb.Append(", ");
                            sb.Append(item.ToInvariantCultureToString());
                        }
                        log = sb.Append("]").ToString();
                        sb.Clear();
                    }
                    else
                        log = $"\r\n{cmd.ReadResult.Value.ToInvariantCultureToString()}";
                }
                else log = $"\r\n{ret.ToInvariantCultureToString()}";
                this.OnNotice(new NoticeEventArgs(
                    NoticeType.Call,
                    exception ?? this.RedisSimpleError,
                    $"{(cmd._redisSocket?.Host ?? "Not connected")} ({sw.ElapsedMilliseconds}ms) > {cmd} {log}",
                    cmd.ReadResult?.Value ?? ret));
            }
        }
        public class NoticeEventArgs : EventArgs
        {
            public NoticeType NoticeType { get; }
            public Exception Exception { get; }
            public string Log { get; }
            public object Tag { get; }

            public NoticeEventArgs(NoticeType noticeType, Exception exception, string log, object tag)
            {
                this.NoticeType = noticeType;
                this.Exception = exception;
                this.Log = log;
                this.Tag = tag;
            }
        }
        public enum NoticeType
        {
            Call, 
        }
        void OnNotice(NoticeEventArgs e)
        {
            this.Notice?.Invoke(this, e);
        }

        #region 序列化写入，反序列化
        public Func<object, string> Serialize;
        public Func<string, Type, object> Deserialize;

        internal object SerializeRedisValue(object value)
        {
            if (value == null) return null;
            var type = value.GetType();
            var typename = type.ToString().TrimEnd(']');
            if (typename == "System.Byte[" ||
                typename == "System.String") return value;

            if (type.IsValueType)
            {
                bool isNullable = typename.StartsWith("System.Nullable`1[");
                var basename = isNullable ? typename.Substring(18) : typename;

                switch (basename)
                {
                    case "System.Boolean": return value.ToString() == "True" ? "1" : "0";
                    case "System.Byte": return value.ToString();
                    case "System.Char": return value.ToString()[0];
                    case "System.Decimal":
                    case "System.Double":
                    case "System.Single":
                    case "System.Int32":
                    case "System.Int64":
                    case "System.SByte":
                    case "System.Int16":
                    case "System.UInt32":
                    case "System.UInt64":
                    case "System.UInt16": return value.ToString();
                    case "System.DateTime": return ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:sszzzz", System.Globalization.DateTimeFormatInfo.InvariantInfo);
                    case "System.DateTimeOffset": return value.ToString();
                    case "System.TimeSpan": return ((TimeSpan)value).Ticks;
                    case "System.Guid": return value.ToString();
                }
            }

            var ser = Adapter.TopOwner.Serialize;
            if (ser != null) return ser(value);
            return value.ConvertTo<string>();
        }
        internal T DeserializeRedisValue<T>(byte[] value, Encoding encoding)
        {
            if (value == null) return default(T);
            var type = typeof(T);
            var typename = type.ToString().TrimEnd(']');
            if (typename == "System.Byte[") return (T)Convert.ChangeType(value, type);
            if (typename == "System.String") return (T)Convert.ChangeType(encoding.GetString(value), type);
            if (typename == "System.Boolean[") return (T)Convert.ChangeType(value.Select(a => a == 49).ToArray(), type);

            var valueStr = encoding.GetString(value);
            if (string.IsNullOrEmpty(valueStr)) return default(T);
            if (type.IsValueType)
            {
                bool isNullable = typename.StartsWith("System.Nullable`1[");
                var basename = isNullable ? typename.Substring(18) : typename;

                bool isElse = false;
                object obj = null;
                switch (basename)
                {
                    case "System.Boolean":
                        if (valueStr == "1") obj = true;
                        else if (valueStr == "0") obj = false;
                        break;
                    case "System.Byte":
                        if (byte.TryParse(valueStr, out var trybyte)) obj = trybyte;
                        break;
                    case "System.Char":
                        if (valueStr.Length > 0) obj = valueStr[0];
                        break;
                    case "System.Decimal":
                        if (Decimal.TryParse(valueStr, out var trydec)) obj = trydec;
                        break;
                    case "System.Double":
                        if (Double.TryParse(valueStr, out var trydb)) obj = trydb;
                        break;
                    case "System.Single":
                        if (Single.TryParse(valueStr, out var trysg)) obj = trysg;
                        break;
                    case "System.Int32":
                        if (Int32.TryParse(valueStr, out var tryint32)) obj = tryint32;
                        break;
                    case "System.Int64":
                        if (Int64.TryParse(valueStr, out var tryint64)) obj = tryint64;
                        break;
                    case "System.SByte":
                        if (SByte.TryParse(valueStr, out var trysb)) obj = trysb;
                        break;
                    case "System.Int16":
                        if (Int16.TryParse(valueStr, out var tryint16)) obj = tryint16;
                        break;
                    case "System.UInt32":
                        if (UInt32.TryParse(valueStr, out var tryuint32)) obj = tryuint32;
                        break;
                    case "System.UInt64":
                        if (UInt64.TryParse(valueStr, out var tryuint64)) obj = tryuint64;
                        break;
                    case "System.UInt16":
                        if (UInt16.TryParse(valueStr, out var tryuint16)) obj = tryuint16;
                        break;
                    case "System.DateTime":
                        if (DateTime.TryParse(valueStr, out var trydt)) obj = trydt;
                        break;
                    case "System.DateTimeOffset":
                        if (DateTimeOffset.TryParse(valueStr, out var trydtos)) obj = trydtos;
                        break;
                    case "System.TimeSpan":
                        if (Int64.TryParse(valueStr, out tryint64)) obj = new TimeSpan(tryint64);
                        break;
                    case "System.Guid":
                        if (Guid.TryParse(valueStr, out var tryguid)) obj = tryguid;
                        break;
                    default:
                        isElse = true;
                        break;
                }

                if (isElse == false)
                {
                    if (obj == null) return default(T);
                    return (T)obj;
                    //return (T)Convert.ChangeType(obj, typeof(T));
                }
            }

            var deser = Adapter.TopOwner.Deserialize;
            if (deser != null) return (T)deser(valueStr, typeof(T));
            return valueStr.ConvertTo<T>();
        }
        #endregion
    }

    public enum ZAddThan { gt, lt }
    public enum BitOpOperation { and, or, xor, not }
    public enum ClusterSetSlotType { importing, migrating, stable, node }
    public enum ClusterResetType { hard, soft }
    public enum ClusterFailOverType { force, takeover }
    public enum ClientUnBlockType { timeout, error }
    public enum ClientReplyType { on, off, skip }
    public enum ClientType { normal, master, slave, pubsub }
    public enum Collation { asc, desc }
    public enum Confirm { yes, no }
    public enum GeoUnit { m, km, mi, ft }
    public enum InsertDirection { before, after }
    public enum KeyType { none, @string, list, set, zset, hash, stream }
    public enum RoleType { Master, Slave, Sentinel }

    /// <summary>
    /// redis version >=6.2: Added the GT and LT options.
    /// </summary>
    public class KeyValue<T>
    {
        public readonly string key;
        public readonly T value;
        public KeyValue(string key, T value) { this.key = key; this.value = value; }
    }
    public class ScanResult<T>
    {
        public readonly long cursor;
        public readonly T[] items;
        public readonly long length;
        public ScanResult(long cursor, T[] items) { this.cursor = cursor; this.items = items; this.length = items.LongLength; }
    }
}
