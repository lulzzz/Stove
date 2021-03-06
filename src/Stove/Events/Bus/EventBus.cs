using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Stove.Events.Bus.Factories;
using Stove.Events.Bus.Factories.Internals;
using Stove.Events.Bus.Handlers;
using Stove.Events.Bus.Handlers.Internals;
using Stove.Extensions;
using Stove.Log;
using Stove.Threading.Extensions;

namespace Stove.Events.Bus
{
	/// <summary>
	///     Implements EventBus as Singleton pattern.
	/// </summary>
	public class EventBus : IEventBus
	{
		/// <summary>
		///     Gets the default <see cref="EventBus" /> instance.
		/// </summary>
		public static EventBus Default = new EventBus();

		/// <summary>
		///     All registered handler factories.
		///     Key: Type of the event
		///     Value: List of handler factories
		/// </summary>
		private readonly ConcurrentDictionary<Type, List<IEventHandlerFactory>> _handlerFactories;

		/// <summary>
		///     Creates a new <see cref="EventBus" /> instance.
		///     Instead of creating a new instace, you can use <see cref="Default" /> to use Global <see cref="EventBus" />.
		/// </summary>
		public EventBus()
		{
			_handlerFactories = new ConcurrentDictionary<Type, List<IEventHandlerFactory>>();
			Logger = NullLogger.Instance;
		}

		/// <summary>
		///     Reference to the Logger.
		/// </summary>
		public ILogger Logger { get; set; }

		/// <inheritdoc />
		public IDisposable Register<TEventData>(Action<TEventData> action) where TEventData : IEventData
		{
			return Register(typeof(TEventData), new ActionEventHandler<TEventData>(action));
		}

		/// <inheritdoc />
		public IDisposable Register<TEventData>(IEventHandler<TEventData> handler) where TEventData : IEventData
		{
			return Register(typeof(TEventData), handler);
		}

		/// <inheritdoc />
		public IDisposable Register<TEventData, THandler>()
			where TEventData : IEventData
			where THandler : IEventHandler<TEventData>, new()
		{
			return Register(typeof(TEventData), new TransientEventHandlerFactory<THandler>());
		}

		/// <inheritdoc />
		public IDisposable Register(Type eventType, IEventHandler handler)
		{
			return Register(eventType, new SingleInstanceHandlerFactory(handler));
		}

		/// <inheritdoc />
		public IDisposable Register<TEventData>(IEventHandlerFactory handlerFactory) where TEventData : IEventData
		{
			return Register(typeof(TEventData), handlerFactory);
		}

		/// <inheritdoc />
		public IDisposable Register(Type eventType, IEventHandlerFactory handlerFactory)
		{
			GetOrCreateHandlerFactories(eventType)
				.Locking(factories => factories.Add(handlerFactory));

			return new FactoryUnregistrar(this, eventType, handlerFactory);
		}

		/// <inheritdoc />
		public void Unregister<TEventData>(Action<TEventData> action) where TEventData : IEventData
		{
			Check.NotNull(action, nameof(action));

			GetOrCreateHandlerFactories(typeof(TEventData))
				.Locking(factories =>
				{
					factories.RemoveAll(
						factory =>
						{
							var singleInstanceFactory = factory as SingleInstanceHandlerFactory;
							if (singleInstanceFactory == null)
							{
								return false;
							}

							var actionHandler = singleInstanceFactory.HandlerInstance as ActionEventHandler<TEventData>;
							if (actionHandler == null)
							{
								return false;
							}

							return actionHandler.Action == action;
						});
				});
		}

		/// <inheritdoc />
		public void Unregister<TEventData>(IEventHandler<TEventData> handler) where TEventData : IEventData
		{
			Unregister(typeof(TEventData), handler);
		}

		/// <inheritdoc />
		public void Unregister(Type eventType, IEventHandler handler)
		{
			GetOrCreateHandlerFactories(eventType)
				.Locking(factories =>
				{
					factories.RemoveAll(
						factory =>
							factory is SingleInstanceHandlerFactory &&
							(factory as SingleInstanceHandlerFactory).HandlerInstance == handler
					);
				});
		}

		/// <inheritdoc />
		public void Unregister<TEventData>(IEventHandlerFactory factory) where TEventData : IEventData
		{
			Unregister(typeof(TEventData), factory);
		}

		/// <inheritdoc />
		public void Unregister(Type eventType, IEventHandlerFactory factory)
		{
			GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Remove(factory));
		}

		/// <inheritdoc />
		public void UnregisterAll<TEventData>() where TEventData : IEventData
		{
			UnregisterAll(typeof(TEventData));
		}

		/// <inheritdoc />
		public void UnregisterAll(Type eventType)
		{
			GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Clear());
		}

		/// <inheritdoc />
		public void Trigger<TEventData>(TEventData eventData) where TEventData : IEventData
		{
			Trigger((object)null, eventData);
		}

		/// <inheritdoc />
		public void Trigger<TEventData>(object eventSource, TEventData eventData) where TEventData : IEventData
		{
			Trigger(typeof(TEventData), eventSource, eventData);
		}

		/// <inheritdoc />
		public void Trigger(Type eventType, IEventData eventData)
		{
			Trigger(eventType, null, eventData);
		}

		/// <inheritdoc />
		public void Trigger(Type eventType, object eventSource, IEventData eventData)
		{
			var exceptions = new List<Exception>();

			TriggerHandlingException(eventType, eventSource, eventData, exceptions);

			if (exceptions.Any())
			{
				if (exceptions.Count == 1)
				{
					exceptions[0].ReThrow();
				}

				throw new AggregateException("More than one error has occurred while triggering the event: " + eventType, exceptions);
			}
		}

		/// <inheritdoc />
		public Task TriggerAsync<TEventData>(TEventData eventData) where TEventData : IEventData
		{
			return TriggerAsync((object)null, eventData);
		}

		/// <inheritdoc />
		public Task TriggerAsync<TEventData>(object eventSource, TEventData eventData) where TEventData : IEventData
		{
			ExecutionContext.SuppressFlow();

			Task task = Task.Factory.StartNew(
				() =>
				{
					try
					{
						Trigger(eventSource, eventData);
					}
					catch (Exception ex)
					{
						Logger.Warn(ex.ToString(), ex);
					}
				});

			ExecutionContext.RestoreFlow();

			return task;
		}

		/// <inheritdoc />
		public Task TriggerAsync(Type eventType, IEventData eventData)
		{
			return TriggerAsync(eventType, null, eventData);
		}

		/// <inheritdoc />
		public Task TriggerAsync(Type eventType, object eventSource, IEventData eventData)
		{
			ExecutionContext.SuppressFlow();

			Task task = Task.Factory.StartNew(
				() =>
				{
					try
					{
						Trigger(eventType, eventSource, eventData);
					}
					catch (Exception ex)
					{
						Logger.Warn(ex.ToString(), ex);
					}
				});

			ExecutionContext.RestoreFlow();

			return task;
		}

		private void TriggerHandlingException(Type eventType, object eventSource, IEventData eventData, List<Exception> exceptions)
		{
			//TODO: This method can be optimized by adding all possibilities to a dictionary.

			eventData.EventSource = eventSource;

			foreach (EventTypeWithEventHandlerFactories handlerFactories in GetHandlerFactories(eventType))
			{
				foreach (IEventHandlerFactory handlerFactory in handlerFactories.EventHandlerFactories)
				{
					IEventHandler eventHandler = handlerFactory.GetHandler();

					try
					{
						if (eventHandler == null)
						{
							throw new Exception($"Registered event handler for event type {handlerFactories.EventType.Name} does not implement IEventHandler<{handlerFactories.EventType.Name}> interface!");
						}

						Type handlerType = typeof(IEventHandler<>).MakeGenericType(handlerFactories.EventType);

						MethodInfo method = handlerType.GetMethod(
							"HandleEvent",
							new[] { handlerFactories.EventType }
						);

						method.Invoke(eventHandler, new object[] { eventData });
					}
					catch (TargetInvocationException ex)
					{
						exceptions.Add(ex.InnerException);
					}
					catch (Exception ex)
					{
						exceptions.Add(ex);
					}
					finally
					{
						handlerFactory.ReleaseHandler(eventHandler);
					}
				}
			}

			//Implements generic argument inheritance. See IEventDataWithInheritableGenericArgument
			if (eventType.GetTypeInfo().IsGenericType &&
			    eventType.GetGenericArguments().Length == 1 &&
			    typeof(IEventDataWithInheritableGenericArgument).IsAssignableFrom(eventType))
			{
				Type genericArg = eventType.GetGenericArguments()[0];
				Type baseArg = genericArg.GetTypeInfo().BaseType;
				if (baseArg != null)
				{
					Type baseEventType = eventType.GetGenericTypeDefinition().MakeGenericType(baseArg);
					object[] constructorArgs = ((IEventDataWithInheritableGenericArgument)eventData).GetConstructorArgs();
					var baseEventData = (IEventData)Activator.CreateInstance(baseEventType, constructorArgs);
					baseEventData.EventTime = eventData.EventTime;
					Trigger(baseEventType, eventData.EventSource, baseEventData);
				}
			}
		}

		private IEnumerable<EventTypeWithEventHandlerFactories> GetHandlerFactories(Type eventType)
		{
			var handlerFactoryList = new List<EventTypeWithEventHandlerFactories>();

			foreach (KeyValuePair<Type, List<IEventHandlerFactory>> handlerFactory in _handlerFactories.Where(hf => ShouldTriggerEventForHandler(eventType, hf.Key)))
			{
				handlerFactoryList.Add(new EventTypeWithEventHandlerFactories(handlerFactory.Key, handlerFactory.Value));
			}

			return handlerFactoryList.ToArray();
		}

		private static bool ShouldTriggerEventForHandler(Type eventType, Type handlerType)
		{
			//Should trigger same type
			if (handlerType == eventType)
			{
				return true;
			}

			//Should trigger for inherited types
			if (handlerType.IsAssignableFrom(eventType))
			{
				return true;
			}

			return false;
		}

		private List<IEventHandlerFactory> GetOrCreateHandlerFactories(Type eventType)
		{
			return _handlerFactories.GetOrAdd(eventType, type => new List<IEventHandlerFactory>());
		}

		private class EventTypeWithEventHandlerFactories
		{
			public EventTypeWithEventHandlerFactories(Type eventType, List<IEventHandlerFactory> eventHandlerFactories)
			{
				EventType = eventType;
				EventHandlerFactories = eventHandlerFactories;
			}

			public Type EventType { get; }

			public List<IEventHandlerFactory> EventHandlerFactories { get; }
		}
	}
}
