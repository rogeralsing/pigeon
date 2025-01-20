﻿//-----------------------------------------------------------------------
// <copyright file="Warning.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Diagnostics;

namespace Akka.Event
{
    /// <summary>
    /// This class represents a Warning log event.
    /// </summary>
    public class Warning : LogEvent
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Warning" /> class.
        /// </summary>
        /// <param name="logSource">The source that generated the log event.</param>
        /// <param name="logClass">The type of logger used to log the event.</param>
        /// <param name="message">The message that is being logged.</param>
        public Warning(string logSource, Type logClass, object message)
            : this(null, logSource, logClass, message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Warning" /> class.
        /// </summary>
        /// <param name="cause">The exception that caused the log event.</param>
        /// <param name="logSource">The source that generated the log event.</param>
        /// <param name="logClass">The type of logger used to log the event.</param>
        /// <param name="message">The message that is being logged.</param>
        public Warning(Exception cause, string logSource, Type logClass, object message)
            : this(cause, logSource, logClass, message, default)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Warning" /> class.
        /// </summary>
        /// <param name="cause">The exception that caused the log event.</param>
        /// <param name="logSource">The source that generated the log event.</param>
        /// <param name="logClass">The type of logger used to log the event.</param>
        /// <param name="message">The message that is being logged.</param>
        /// <param name="context">The current <see cref="Activity"/>'s context, if one is present.</param>
        public Warning(Exception cause, string logSource, Type logClass, object message, in ActivityContext context)
        {
            LogSource = logSource;
            LogClass = logClass;
            Message = message;
            Cause = cause;
            ActivityContext = context;
        }

        /// <summary>
        /// Retrieves the <see cref="Akka.Event.LogLevel" /> used to classify this event.
        /// </summary>
        /// <returns>
        /// The <see cref="Akka.Event.LogLevel" /> used to classify this event.
        /// </returns>
        public override LogLevel LogLevel()
        {
            return Event.LogLevel.WarningLevel;
        }
    }
}
