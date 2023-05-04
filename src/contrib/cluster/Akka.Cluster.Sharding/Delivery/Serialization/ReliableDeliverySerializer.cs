﻿// -----------------------------------------------------------------------
//  <copyright file="ReliableDeliverySerializer.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2023 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2023 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Akka.Actor;
using Akka.Delivery;
using Akka.Remote.Serialization;
using Akka.Serialization;
using Akka.Util;
using Akka.Cluster.Sharding.Serialization.Proto.Msg;
using Akka.Delivery.Internal;
using Akka.Remote.Serialization.Proto.Msg;
using Google.Protobuf;

namespace Akka.Cluster.Sharding.Delivery.Serialization;

/// <summary>
/// INTERNAL API
/// </summary>
internal sealed class ReliableDeliverySerializer : SerializerWithStringManifest
{
    private readonly WrappedPayloadSupport _payloadSupport;
    private const string SequencedMessageManifest = "a";
    private const string AckManifest = "b";
    private const string RequestManifest = "c";
    private const string ResendManifest = "d";
    private const string RegisterConsumerManifest = "e";

    // durable queue manifests
    private const string DurableQueueMessageSentManifest = "f";
    private const string DurableQueueConfirmedManifest = "g";
    private const string DurableQueueStateManifest = "h";
    private const string DurableQueueCleanupManifest = "i";

    public ReliableDeliverySerializer(ExtendedActorSystem system) : base(system)
    {
        _payloadSupport = new WrappedPayloadSupport(system);
    }

    public override byte[] ToBinary(object obj)
    {
        switch (obj)
        {
            case ConsumerController.ISequencedMessage sequencedMessage:
                return SequencedMessageToProto(sequencedMessage).ToByteArray();
            case ProducerController.Ack ack:
                return AckToProto(ack).ToByteArray();
            case ProducerController.Request request:
                return RequestToProto(request).ToByteArray();
            case ProducerController.Resend resend:
                return ResendToProto(resend).ToByteArray();
            case ProducerController.IRegisterConsumer registerConsumer:
                return RegisterConsumerToProto(registerConsumer).ToByteArray();
        }
    }

    public override object FromBinary(byte[] bytes, string manifest)
    {
        switch (manifest)
        {
            case SequencedMessageManifest:
                return SequencedMessageFromBinary(bytes);
            case AckManifest:
                return AckFromProto(Ack.Parser.ParseFrom(bytes));
            case RequestManifest:
                return RequestFromProto(Request.Parser.ParseFrom(bytes));
            case ResendManifest:
                return ResendFromProto(Resend.Parser.ParseFrom(bytes));
            case RegisterConsumerManifest:
                return RegisterConsumerFromProto(RegisterConsumer.Parser.ParseFrom(bytes));
        }
    }

    public override string Manifest(object o)
    {
        switch (o)
        {
            case ConsumerController.ISequencedMessage _:
                return SequencedMessageManifest;
            case ProducerController.Ack _:
                return AckManifest;
            case ProducerController.Request _:
                return RequestManifest;
            case ProducerController.Resend _:
                return ResendManifest;
            case ProducerController.IRegisterConsumer _:
                return RegisterConsumerManifest;
            case DurableProducerQueue.IMessageSent _:
                return DurableQueueMessageSentManifest;
            case DurableProducerQueue.Confirmed _:
                return DurableQueueConfirmedManifest;
            case DurableProducerQueue.IState _:
                return DurableQueueStateManifest;
            case DurableProducerQueue.Cleanup _:
                return DurableQueueCleanupManifest;
            default:
                throw new ArgumentException($"Can't serialize object of type [{o.GetType()}] in [{GetType()}]");
        }
    }

    #region ToBinary

    private static TypeDescriptor GetTypeDescriptor(Type t)
    {
        var typeInfo = new TypeDescriptor();
        typeInfo.TypeName = t.TypeQualifiedName();
        return typeInfo;
    }

    private SequencedMessage SequencedMessageToProto(ConsumerController.ISequencedMessage sequencedMessage)
    {
        var msgType = sequencedMessage.PayloadType;
        
        MethodInfo method = typeof(ReliableDeliverySerializer).GetMethod(nameof(SequencedMessageToProtoGeneric), BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo generic = method.MakeGenericMethod(msgType);
        return (SequencedMessage)generic.Invoke(this, new object[] { sequencedMessage });
    }

    private SequencedMessage SequencedMessageToProtoGeneric<T>(ConsumerController.ISequencedMessage uncasted)
    {
        var sequencedMessage = (ConsumerController.SequencedMessage<T>)uncasted;
        var sequencedMessageBuilder = new SequencedMessage();
        var typeDescriptor = GetTypeDescriptor(typeof(T));
        var payload = sequencedMessage.Message.IsMessage
            ? _payloadSupport.PayloadToProto(sequencedMessage.Message.Message)
            : ChunkedMessageToProto(sequencedMessage.Message.Chunk!.Value);

        sequencedMessageBuilder.TypeInfo = typeDescriptor;
        sequencedMessageBuilder.SeqNr = sequencedMessage.SeqNr;
        sequencedMessageBuilder.Message = payload;
        sequencedMessageBuilder.Ack = sequencedMessage.Ack;
        sequencedMessageBuilder.FirstChunk = sequencedMessage.IsFirstChunk;
        sequencedMessageBuilder.LastChunk = sequencedMessage.IsLastChunk;
        sequencedMessageBuilder.ProducerId = sequencedMessage.ProducerId;
        sequencedMessageBuilder.First = sequencedMessage.First;
        sequencedMessageBuilder.ProducerControllerRef =
            Akka.Serialization.Serialization.SerializedActorPath(sequencedMessage.ProducerController);
        sequencedMessageBuilder.IsChunk = !sequencedMessage.Message.IsMessage;
        return sequencedMessageBuilder;
    }

    private static Payload ChunkedMessageToProto(ChunkedMessage message)
    {
        var builder = new Payload();
        // TODO: reduce allocations
        builder.Message = ByteString.CopyFrom(message.SerializedMessage.ToArray());
        builder.MessageManifest = ByteString.CopyFromUtf8(message.Manifest);
        builder.SerializerId = message.SerializerId;
        return builder;
    }
    
    // create method to convert Ack to Proto
    private static Ack AckToProto(ProducerController.Ack ack)
    {
        var builder = new Ack();
        builder.ConfirmedSeqNr = ack.ConfirmedSeqNr;
        return builder;
    }
    
    // create method to convert Request to Proto
    private static Request RequestToProto(ProducerController.Request request)
    {
        var builder = new Request();
        builder.RequestUpToSeqNr = request.RequestUpToSeqNr;
        builder.ConfirmedSeqNr = request.ConfirmedSeqNr;
        builder.SupportResend = request.SupportResend;
        builder.ViaTimeout = request.ViaTimeout;
        return builder;
    }
    
    // create method to convert Resend to Proto
    private static Resend ResendToProto(ProducerController.Resend resend)
    {
        var builder = new Resend();
        builder.FromSeqNr = resend.FromSeqNr;
        return builder;
    }
    
    // create method to convert RegisterConsumer to Proto
    private RegisterConsumer RegisterConsumerToProto(ProducerController.IRegisterConsumer registerConsumer)
    {
        MethodInfo method = typeof(ReliableDeliverySerializer).GetMethod(nameof(RegisterConsumerToProtoGeneric), BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo generic = method.MakeGenericMethod(registerConsumer.ConsumerType);
        return (RegisterConsumer)generic.Invoke(this, new object[] { registerConsumer });
    }
    
    private static RegisterConsumer RegisterConsumerToProtoGeneric<T>(ProducerController.IRegisterConsumer uncasted)
    {
        var registerConsumer = (ProducerController.RegisterConsumer<T>)uncasted;
        var registerConsumerBuilder = new RegisterConsumer();
        var typeDescriptor = GetTypeDescriptor(typeof(T));
        registerConsumerBuilder.TypeInfo = typeDescriptor;
        registerConsumerBuilder.ConsumerControllerRef = Akka.Serialization.Serialization.SerializedActorPath(registerConsumer.ConsumerController);
        return registerConsumerBuilder;
    }

    #endregion

    #region FromBinary

    private static Type GetTypeFromDescriptor(TypeDescriptor t)
    {
        // if we can't find the type, blow up
        var type = Type.GetType(t.TypeName, true);
        return type;
    }

    private ConsumerController.ISequencedMessage SequencedMessageFromBinary(byte[] bytes)
    {
        var seqMsg = SequencedMessage.Parser.ParseFrom(bytes);
        var type = GetTypeFromDescriptor(seqMsg.TypeInfo);
        
        var method = typeof(ReliableDeliverySerializer).GetMethod(nameof(SequencedMessageFromProto), BindingFlags.NonPublic | BindingFlags.Instance)!;
        var generic = method.MakeGenericMethod(type);
        return (ConsumerController.ISequencedMessage)generic.Invoke(this, new object[] { seqMsg });
    }

    private ConsumerController.ISequencedMessage SequencedMessageFromProto<T>(SequencedMessage seqMsg)
    {
        if (seqMsg.IsChunk)
        {
            var chunk = new ChunkedMessage(IO.ByteString.CopyFrom(seqMsg.Message.Message.ToByteArray()),
                seqMsg.FirstChunk,
                seqMsg.LastChunk, seqMsg.Message.SerializerId, seqMsg.Message.MessageManifest.ToString());
           return ConsumerController.SequencedMessage<T>.FromChunkedMessage(seqMsg.ProducerId, seqMsg.SeqNr, chunk,
                seqMsg.First, seqMsg.Ack, ResolveActorRef(seqMsg.ProducerControllerRef));
        }

        var msg = (T)_payloadSupport.PayloadFrom(seqMsg.Message);
        return new ConsumerController.SequencedMessage<T>(seqMsg.ProducerId, seqMsg.SeqNr, msg,
            seqMsg.First, seqMsg.Ack, ResolveActorRef(seqMsg.ProducerControllerRef));
    }

    private IActorRef ResolveActorRef(string path)
    {
        return system.Provider.ResolveActorRef(path);
    }
    
    private static ProducerController.Ack AckFromProto(Ack ack)
    {
        return new ProducerController.Ack(ack.ConfirmedSeqNr);
    }
    
    private static ProducerController.Request RequestFromProto(Request request)
    {
        return new ProducerController.Request(request.ConfirmedSeqNr, request.RequestUpToSeqNr, request.SupportResend, request.ViaTimeout);
    }
    
    private static ProducerController.Resend ResendFromProto(Resend resend)
    {
        return new ProducerController.Resend(resend.FromSeqNr);
    }
    
    private ProducerController.IRegisterConsumer RegisterConsumerFromProto(RegisterConsumer registerConsumer)
    {
        var type = GetTypeFromDescriptor(registerConsumer.TypeInfo);
        
        var actorRef = ResolveActorRef(registerConsumer.ConsumerControllerRef);
        
        // make a generic ProducerController.RegisterConsumer<T> and return it
        var genericRegisterConsumer = typeof(ProducerController.RegisterConsumer<>).MakeGenericType(type);
        return (ProducerController.IRegisterConsumer)Activator.CreateInstance(genericRegisterConsumer, actorRef);
    }

    #endregion
}