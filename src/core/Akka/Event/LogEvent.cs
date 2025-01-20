//-----------------------------------------------------------------------
// <copyright file="LogEvent.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Akka.Actor;
using Microsoft.Extensions.ObjectPool;

namespace Akka.Event
{
    /// <summary>
    /// INTERNAL API.
    /// 
    /// Avoids redundant parsing of log levels and other frequently-used log items
    /// </summary>
    internal static class LogFormats
    {
        public static readonly IReadOnlyDictionary<LogLevel, string> PrettyPrintedLogLevel;

        static LogFormats()
        {
            var dict = new Dictionary<LogLevel, string>();
            foreach(LogLevel i in Enum.GetValues(typeof(LogLevel)))
            {
                dict.Add(i, Enum.GetName(typeof(LogLevel), i).Replace("Level", "").ToUpperInvariant());
            }
            PrettyPrintedLogLevel = dict;
        }

        public static string PrettyNameFor(this LogLevel level)
        {
            return PrettyPrintedLogLevel[level];
        }
        
        /// <summary>
        /// For formatting the <see cref="LogEvent"/> instances.
        /// </summary>
        public static readonly ObjectPool<StringBuilder> StringBuilderPool = new DefaultObjectPoolProvider().CreateStringBuilderPool(10, 1024);
    }

    /// <summary>
    /// This class represents a logging event in the system.
    /// </summary>
    public abstract class LogEvent : INoSerializationVerificationNeeded
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LogEvent" /> class.
        /// </summary>
        protected LogEvent()
        {
            Timestamp = DateTime.UtcNow;
            Thread = Thread.CurrentThread;
        }

        /// <summary>
        /// The exception that caused the log event. Can be <c>null</c>
        /// </summary>
        public Exception Cause { get; protected set; }

        /// <summary>
        /// The timestamp that this event occurred.
        /// </summary>
        public DateTime Timestamp { get; private set; }

        /// <summary>
        /// The thread where this event occurred.
        /// </summary>
        public Thread Thread { get; private set; }

        /// <summary>
        /// The source that generated this event.
        /// </summary>
        public string LogSource { get; protected set; }

        /// <summary>
        /// The type that generated this event.
        /// </summary>
        public Type LogClass { get; protected set; }

        /// <summary>
        /// The message associated with this event.
        /// </summary>
        public object Message { get; protected set; }

        /// <summary>
        /// Retrieves the <see cref="Akka.Event.LogLevel" /> used to classify this event.
        /// </summary>
        /// <returns>The <see cref="Akka.Event.LogLevel" /> used to classify this event.</returns>
        public abstract LogLevel LogLevel();
        
        /// <summary>
        /// Used to help correlate Akka.NET logs with OpenTelemetry traces.
        /// </summary>
        /// <remarks>
        /// Works best in combination with manual instrumentation of Akka.NET actors or
        /// <see href="https://phobos.petabridge.com/"/> for automatic instrumentation.
        /// </remarks>
        public ActivityContext ActivityContext { get; protected set; }

        /// <summary>
        /// Returns a <see cref="string" /> that represents this LogEvent.
        /// </summary>
        /// <returns>A <see cref="string" /> that represents this LogEvent.</returns>
        public override string ToString()
        {
            return FormatLog(this);
        }

        private static string FormatLog(LogEvent log)
        {
            var stringBuilder = LogFormats.StringBuilderPool.Get();
            try
            {
                // loglevel and timestamp go first followed by thread id and log source
                stringBuilder.AppendFormat("[{0}][{1:MM/dd/yyyy HH:mm:ss.fffK}][Thread {2:0000}][{3}]", log.LogLevel().PrettyNameFor(),
                    log.Timestamp, log.Thread.ManagedThreadId, log.LogSource);
            
                // next: we do the OpenTelemetry ActivityContext, if it's present
                if(log.ActivityContext != default)
                {
                    stringBuilder.AppendFormat("[TraceId={0}, SpanId={1}, TraceFlags={2}]", log.ActivityContext.TraceId.ToHexString(), log.ActivityContext.SpanId.ToHexString(), log.ActivityContext.TraceFlags);
                }
            
                // then the message
                stringBuilder.Append(' ').Append(log.Message);
                if(log.Cause != null)
                {
                    stringBuilder.AppendLine().Append("Cause: ").Append(log.Cause);
                }
                
                return stringBuilder.ToString();
            }
            finally
            {
                LogFormats.StringBuilderPool.Return(stringBuilder);
            }
        }
    }
}
