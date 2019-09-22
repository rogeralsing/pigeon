﻿// -----------------------------------------------------------------------
//  <copyright file="Times.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

namespace Akka.MultiNodeTestRunner.AzureDevOps.Models
{
    using System;
    using System.Xml.Linq;
    using static XmlHelper;

    public class Times : ITestEntity
    {
        public Times()
        {
            var now = DateTime.UtcNow;
            
            Creation = now;
            Queuing = now;
            Start = now;
            Finish = now;
        }

        public DateTime Creation { get; set; }
        public DateTime Queuing { get; set; }
        public DateTime Start { get; set; }
        public DateTime Finish { get; set; }

        public XElement Serialize() => Elem("Times",
            Attr("creation", Creation.ToString("O")),
            Attr("queuing", Queuing.ToString("O")),
            Attr("start", Start.ToString("O")),
            Attr("finish", Finish.ToString("O"))
        );
    }
}