﻿// -----------------------------------------------------------------------
//  <copyright file="TestList.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

namespace Akka.MultiNodeTestRunner.AzureDevOps.Models
{
    using System.Xml.Linq;
    using static XmlHelper;

    public class TestList : ITestEntity
    {
        public TestList(string name)
        {
            Name = name;
        }

        public Identifier Id { get; } = Identifier.Create();
        public string Name { get; }

        public XElement Serialize() => Elem("TestList",
            Attr("id", Id),
            Attr("name", Name)
        );
    }
}