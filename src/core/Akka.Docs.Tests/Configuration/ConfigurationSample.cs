﻿// //-----------------------------------------------------------------------
// // <copyright file="DocsSample.cs" company="Akka.NET Project">
// //     Copyright (C) 2013-2020 .NET Foundation <https://github.com/akkadotnet/akka.net>
// // </copyright>
// //-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hocon;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;

namespace DocsExamples.Configuration
{
    public class ConfigurationSample
    {
        [Fact]
        public void StringSubstitutionSample()
        {
            // ${string_bar} will be substituted by 'bar' and concatenated with `foo` into `foobar`
            var hoconString = @"
string_bar = bar
string_foobar = foo${string_bar}
";
            var config = ConfigurationFactory.ParseString(hoconString); // This config uses ConfigurationFactory as a helper
            config.GetString("string_foobar").Should().Be("foobar");
        }

        [Fact]
        public void ArraySubstitutionSample()
        {
            // ${a} will be substituted by the array [1, 2] and concatenated with [3, 4] to create [1, 2, 3, 4]
            var hoconString = @"
a = [1,2]
b = ${a} [3, 4]";
            Config config = hoconString; // This Config uses implicit conversion from string directly into a Config object
            (new[] { 1, 2, 3, 4 }).ShouldAllBeEquivalentTo(config.GetIntList("b"));
        }

        [Fact]
        public void ObjectMergeSubstitutionSample()
        {
            // ${a} will be substituted by hocon object 'a' and merged with object 'b'
            var hoconString = @"
a.a : 1
b.b : 2
b : ${a}
";
            var expectedHoconString = @"{
  a : {
    a : 1
  },
  b : {
    b : 2,
    a : 1
  }
}";
            Config config = hoconString;
            expectedHoconString.ShouldBeEquivalentTo(config.Value.ToString(1, 2));
        }

        [Fact]
        public void SelfReferencingSubstitutionWithString()
        {
            // This is not an invalid substitution, it is a self referencing substitution, you can think of it as `a = a + 'bar'`
            // ${a} will be substituted with its previous value, which is 'foo', concatenated with 'bar' to make 'foobar', 
            // and then stored back into a
            var hoconString = @"
a = foo
a = ${a}bar
";
            Config config = hoconString;
            config.GetString("a").Should().Be("foobar");
        }

        [Fact]
        public void SelfReferencingSubstitutionWithArray()
        {
            // This is not an invalid substitution, it is a self referencing substitution, you can think of it as `a = a + [3, 4]`
            // ${a} will be substituted with its previous value, which is [1, 2], concatenated with [3, 4] to make [1, 2, 3, 4], 
            // and then stored back into a
            var hoconString = @"
a = [1, 2]
a = ${a} [3, 4]
";
            Config config = hoconString;
            (new int[] { 1, 2, 3, 4 }).ShouldAllBeEquivalentTo(config.GetIntList("a"));
        }

        [Fact]
        public void EnvironmentVariableSample()
        {
            // This substitution will be subtituted by the environment variable.
            var hoconString = "from_environment = ${MY_ENV_VAR}";
            var value = 1000;
            // Set environment variable named `ENVIRONMENT_VAR` with the string value 1000
            Environment.SetEnvironmentVariable("MY_ENV_VAR", value.ToString()); 
            try
            {
                Config config = hoconString;
                // Value obtained from environment variable should be 1000
                config.GetInt("from_environment").Should().Be(value); 
            }
            finally
            {
                // Delete the environment variable.
                Environment.SetEnvironmentVariable("MY_ENV_VAR", null); 
            }
        }

        [Fact]
        public void BlockedEnvironmentVariableSample()
        {
            var hoconString = @"
# This property blocks `MY_ENV_VAR` from being resolved from the environment variable
MY_ENV_VAR = null

# This substitution will not be populated with the environment variable because it is blocked
from_environment = ${MY_ENV_VAR} 
";
            Environment.SetEnvironmentVariable("MY_ENV_VAR", "1000");
            try
            {
                Config config = hoconString;
                // Environment variable is blocked by the previous declaration, it will contain null
                config.GetString("from_environment").Should().BeNull(); 
            }
            finally
            {
                Environment.SetEnvironmentVariable("MY_ENV_VAR", null);
            }
        }

        [Fact]
        public void UnsolvableSubstitutionWillThrowSample()
        {
            // This substitution will throw an exception because it is a required substitution,
            // and we can not resolve it, even when checking for environment variables.
            var hoconString = "from_environment = ${MY_ENV_VAR}";

            Assert.Throws<HoconParserException>(() =>
            {
                Config config = hoconString;
            }).Message.Should().StartWith("Unresolved substitution");
        }
    }
}
