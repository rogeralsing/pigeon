﻿//-----------------------------------------------------------------------
// <copyright file="SerializationSupport.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2023 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2023 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Akka.Actor;
using Akka.DistributedData.Serialization.Proto.Msg;
using Akka.Serialization;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using Google.Protobuf;
using Address = Akka.Actor.Address;
using MemoryStream = System.IO.MemoryStream;
using UniqueAddress = Akka.Cluster.UniqueAddress;

namespace Akka.DistributedData.Serialization
{
    
    /// <summary>
    /// INTERNAL API.
    ///
    /// Used to support the DData serializers.
    /// </summary>
    internal sealed class SerializationSupport
    {
        public SerializationSupport(ExtendedActorSystem system)
        {
            System = system;
        }

        private const int BufferSize = 1024 * 4;

        public ExtendedActorSystem System { get; }

        private volatile Akka.Serialization.Serialization _ser;

        public Akka.Serialization.Serialization Serialization
        {
            get
            {
                if (_ser == null)
                {
                    _ser = new Akka.Serialization.Serialization(System);
                }

                return _ser;
            }
        }

        private volatile string _protocol;

        public string AddressProtocol
        {
            get
            {
                if (_protocol == null)
                    _protocol = System.Provider.DefaultAddress.Protocol;
                return _protocol;
            }
        }

        private volatile Information _transportInfo;

        public Information TransportInfo
        {
            get
            {
                if (_transportInfo == null)
                {
                    var address = System.Provider.DefaultAddress;
                    _transportInfo = new Information(address, System);
                }

                return _transportInfo;
            }
        }

        public static byte[] Compress(IMessage msg)
        {
            using (var compressedBuffer = new ArrayPoolBufferWriter<byte>(1024))
            {
                using (var memStream = compressedBuffer.AsStream())
                {
                    using (var gzip = new GZipStream(memStream,
                               CompressionMode.Compress))
                    {
                        msg.WriteTo(gzip);
                    }
                }

                return compressedBuffer.WrittenMemory.ToArray();
            }
        }
        
        public static byte[] Decompress(byte[] input)
        {
            using (var buf = DecompressWithRentedPool(input))
            {
                return buf.Memory.ToArray();
            }
        }
        
        public static IMemoryOwner<byte> DecompressWithRentedPool(ReadOnlyMemory<byte> input)
        {
            ArrayPoolBufferWriter<byte> decompressedBufferWriter = null;
            bool failed = true;
            try
            {
                decompressedBufferWriter =
                    new ArrayPoolBufferWriter<byte>(4096);
                using (var inputStr = input.AsStream())
                using (var gzipStream =
                       new GZipStream(inputStr, CompressionMode.Decompress))
                {
                    while (gzipStream.CanRead)
                    {
                        var read =
                            gzipStream.Read(
                                decompressedBufferWriter.GetSpan(4096));
                        if (read > 0)
                        {
                            decompressedBufferWriter.Advance(read);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                failed = false;
                return decompressedBufferWriter;
            }
            finally
            {
                if (failed)
                    decompressedBufferWriter?.Dispose();
            }
           
        }

        public static Proto.Msg.Address AddressToProto(Address address)
        {
            if (string.IsNullOrEmpty(address.Host) || !address.Port.HasValue)
                throw new ArgumentOutOfRangeException(
                    $"Address [{address}] could not be serialized: host or port missing.");

            return new Proto.Msg.Address() { Hostname = address.Host, Port = address.Port.Value };
        }

        public Address AddressFromProto(Proto.Msg.Address address)
        {
            return new Address(AddressProtocol, System.Name, address.Hostname, address.Port);
        }

        public static Proto.Msg.UniqueAddress UniqueAddressToProto(UniqueAddress address)
        {
            return new Proto.Msg.UniqueAddress() { Address = AddressToProto(address.Address), Uid = address.Uid };
        }

        public UniqueAddress UniqueAddressFromProto(Proto.Msg.UniqueAddress address)
        {
            return new UniqueAddress(AddressFromProto(address.Address), (int)address.Uid);
        }

        public static Proto.Msg.VersionVector VersionVectorToProto(VersionVector versionVector)
        {
            var b = new Proto.Msg.VersionVector();

            using (var enumerator = versionVector.VersionEnumerator)
            {
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    b.Entries.Add(new Proto.Msg.VersionVector.Types.Entry()
                    {
                        Node = UniqueAddressToProto(current.Key),
                        Version = current.Value
                    });
                }
            }

            return b;
        }

        public VersionVector VersionVectorFromProto(Proto.Msg.VersionVector versionVector)
        {
            var entries = versionVector.Entries;
            if (entries.Count == 0)
                return VersionVector.Empty;
            if (entries.Count == 1)
                return new SingleVersionVector(UniqueAddressFromProto(versionVector.Entries[0].Node),
                    versionVector.Entries[0].Version);
            var versions = entries.ToDictionary(x => UniqueAddressFromProto(x.Node), v => v.Version);
            return new MultiVersionVector(versions);
        }

        public VersionVector VersionVectorFromBinary(byte[] bytes)
        {
            return VersionVectorFromProto(Proto.Msg.VersionVector.Parser.ParseFrom(bytes));
        }

        public IActorRef ResolveActorRef(string path)
        {
            return System.Provider.ResolveActorRef(path);
        }

        private static readonly NonBlocking.ConcurrentDictionary<string, ByteString>
            _manifestBsCache = new();
        public Proto.Msg.OtherMessage OtherMessageToProto(object msg)
        {
            // Serialize actor references with full address information (defaultAddress).
            // When sending remote messages currentTransportInformation is already set,
            // but when serializing for digests or DurableStore it must be set here.
            var oldInfo = Akka.Serialization.Serialization.CurrentTransportInformation;
            try
            {
                if (oldInfo == null)
                    Akka.Serialization.Serialization.CurrentTransportInformation =
                        System.Provider.SerializationInformation;
                return BuildOtherImpl(msg,Serialization);
            }
            finally
            {
                Akka.Serialization.Serialization.CurrentTransportInformation = oldInfo;
            }
        }

        private static OtherMessage BuildOtherImpl(object msg,
            Akka.Serialization.Serialization serialization)
        {
            var m = new OtherMessage();
            var msgSerializer = serialization.FindSerializerFor(msg);
            m.SerializerId = msgSerializer.Identifier;

            m.EnclosedMessage =
                UnsafeByteOperations.UnsafeWrap(
                    msgSerializer.ToBinary(msg)); //ByteString.CopyFrom(msgSerializer.ToBinary(msg));

            var ms = Akka.Serialization.Serialization.ManifestFor(msgSerializer, msg);
            if (!string.IsNullOrEmpty(ms))
                m.MessageManifest = _manifestBsCache.GetOrAdd(ms,
                    static k => ByteString.CopyFromUtf8(k)); 
            return m;
        }

        public object OtherMessageFromBytes(byte[] other)
        {
            return OtherMessageFromProto(OtherMessage.Parser.ParseFrom(other));
        }

        public object OtherMessageFromProto(Proto.Msg.OtherMessage other)
        {
            var manifest = other.MessageManifest != null ? other.MessageManifest.ToStringUtf8() : string.Empty;
            return Serialization.Deserialize(other.EnclosedMessage.ToByteArray(), other.SerializerId, manifest);
        }
    }
}
