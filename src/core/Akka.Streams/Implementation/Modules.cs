﻿using System;
using System.Collections.Immutable;
using System.Reactive.Streams;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Streams.Actors;

namespace Akka.Streams.Implementation
{
    internal interface ISourceModule
    {
        Shape Shape { get; }
        IPublisher Create(MaterializationContext context, out object materializer);
    }

    internal abstract class SourceModule<TOut, TMat> : AtomicModule, ISourceModule
    {
        private readonly SourceShape<TOut> _shape;

        protected SourceModule(SourceShape<TOut> shape)
        {
            _shape = shape;
        }

        public override Shape Shape { get { return _shape; } }

        // This is okay since the only caller of this method is right below.
        protected abstract SourceModule<TOut, TMat> NewInstance(SourceShape<TOut> shape);
        public abstract IPublisher<TOut> Create(MaterializationContext context, out TMat materializer);

        IPublisher ISourceModule.Create(MaterializationContext context, out object materializer)
        {
            TMat m;
            var result = Create(context, out m);
            materializer = m;
            return result;
        }

        public override IModule ReplaceShape(Shape shape)
        {
            if (Equals(shape, Shape)) return this;
            else throw new NotSupportedException("cannot replace the shape of a Source, you need to wrap it in a Graph for that");
        }

        public override IModule CarbonCopy()
        {
            return NewInstance(new SourceShape<TOut>(Outlet.Create<TOut>(_shape.Outlet.CarbonCopy())));
        }
        
        protected SourceShape<TOut> AmendShape(Attributes attributes)
        {
            var thisN = Attributes.GetNameOrDefault(null);
            var thatN = attributes.GetNameOrDefault(null);

            return thatN == null || thatN == thisN
                ? _shape
                : new SourceShape<TOut>(new Outlet<TOut>(thatN + ".out"));
        }
    }

    /// <summary>
    /// Holds a `Subscriber` representing the input side of the flow. The `Subscriber` can later be connected to an upstream `Publisher`.
    /// </summary>
    /// <typeparam name="TOut"></typeparam>
    internal sealed class SubscriberSource<TOut> : SourceModule<TOut, ISubscriber<TOut>>
    {
        private readonly Attributes _attributes;

        public SubscriberSource(Attributes attributes, SourceShape<TOut> shape) : base(shape)
        {
            _attributes = attributes;
        }

        public override Attributes Attributes { get { return _attributes; } }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new SubscriberSource<TOut>(attributes, AmendShape(attributes));
        }

        protected override SourceModule<TOut, ISubscriber<TOut>> NewInstance(SourceShape<TOut> shape)
        {
            return new SubscriberSource<TOut>(Attributes, shape);
        }

        public override IPublisher<TOut> Create(MaterializationContext context, out ISubscriber<TOut> materializer)
        {
            var processor = new VirtualProcessor<TOut>();
            materializer = processor;
            return processor;
        }
    }

    /// <summary>
    /// Construct a transformation starting with given publisher. The transformation steps are executed 
    /// by a series of <see cref="IProcessor{T1,T2}"/> instances that mediate the flow of elements 
    /// downstream and the propagation of back-pressure upstream.
    /// </summary>
    /// <typeparam name="TOut"></typeparam>
    internal sealed class PublisherSource<TOut> : SourceModule<TOut, Unit>
    {
        private readonly IPublisher<TOut> _publisher;
        private readonly Attributes _attributes;

        public PublisherSource(IPublisher<TOut> publisher, Attributes attributes, SourceShape<TOut> shape) : base(shape)
        {
            _publisher = publisher;
            _attributes = attributes;
        }

        public override Attributes Attributes { get { return _attributes; } }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new PublisherSource<TOut>(_publisher, _attributes, AmendShape(_attributes));
        }

        protected override SourceModule<TOut, Unit> NewInstance(SourceShape<TOut> shape)
        {
            return new PublisherSource<TOut>(_publisher, _attributes, shape);
        }

        public override IPublisher<TOut> Create(MaterializationContext context, out Unit materializer)
        {
            materializer = Unit.Instance;
            return _publisher;
        }
    }

    internal sealed class MaybeSource<TOut> : SourceModule<TOut, TaskCompletionSource<TOut>>
    {
        private readonly Attributes _attributes;

        public MaybeSource(Attributes attributes, SourceShape<TOut> shape) : base(shape)
        {
            _attributes = attributes;
        }

        public override Attributes Attributes { get { return _attributes; } }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new MaybeSource<TOut>(attributes, AmendShape(attributes));
        }

        protected override SourceModule<TOut, TaskCompletionSource<TOut>> NewInstance(SourceShape<TOut> shape)
        {
            return new MaybeSource<TOut>(_attributes, shape);
        }

        public override IPublisher<TOut> Create(MaterializationContext context, out TaskCompletionSource<TOut> materializer)
        {
            materializer = new TaskCompletionSource<TOut>();
            return new MaybePublisher<TOut>(materializer, Attributes.GetNameOrDefault("MaybeSource"));
        }
    }

    /// <summary>
    /// Creates and wraps an actor into <see cref="IPublisher{T}"/> from the given <see cref="Props"/>, which should be props for an <see cref="ActorPublisher{T}"/>.
    /// </summary>
    /// <typeparam name="TOut"></typeparam>
    internal sealed class ActorPublisherSource<TOut> : SourceModule<TOut, IActorRef>
    {
        private readonly Props _props;
        private readonly Attributes _attributes;

        public ActorPublisherSource(Props props, Attributes attributes, SourceShape<TOut> shape) : base(shape)
        {
            _props = props;
            _attributes = attributes;
        }

        public override Attributes Attributes { get { return _attributes; } }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new ActorPublisherSource<TOut>(_props, attributes, AmendShape(attributes));
        }

        protected override SourceModule<TOut, IActorRef> NewInstance(SourceShape<TOut> shape)
        {
            return new ActorPublisherSource<TOut>(_props, _attributes, shape);
        }

        public override IPublisher<TOut> Create(MaterializationContext context, out IActorRef materializer)
        {
            var publisherRef = ActorMaterializer.Downcast(context.Materializer).ActorOf(context, _props);
            materializer = publisherRef;
            return new ActorPublisherImpl<TOut>(publisherRef);
        }
    }

    internal sealed class ActorRefSource<TOut> : SourceModule<TOut, IActorRef>
    {
        private readonly int _bufferSize;
        private readonly OverflowStrategy _overflowStrategy;
        private readonly Attributes _attributes;

        public ActorRefSource(int bufferSize, OverflowStrategy overflowStrategy, Attributes attributes, SourceShape<TOut> shape) : base(shape)
        {
            _bufferSize = bufferSize;
            _overflowStrategy = overflowStrategy;
            _attributes = attributes;
        }

        public override Attributes Attributes { get { return _attributes; } }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new ActorRefSource<TOut>(_bufferSize, _overflowStrategy, attributes, AmendShape(attributes));
        }

        protected override SourceModule<TOut, IActorRef> NewInstance(SourceShape<TOut> shape)
        {
            return new ActorRefSource<TOut>(_bufferSize, _overflowStrategy, _attributes, shape);
        }

        public override IPublisher<TOut> Create(MaterializationContext context, out IActorRef materializer)
        {
            var mat = ActorMaterializer.Downcast(context.Materializer);
            materializer = mat.ActorOf(context, ActorRefSourceActor.Props(_bufferSize, _overflowStrategy, mat.Settings));
            return new ActorPublisherImpl<TOut>(materializer);
        }
    }
}