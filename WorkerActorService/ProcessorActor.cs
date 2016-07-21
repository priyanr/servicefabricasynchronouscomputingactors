﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

#region Using Directives



#endregion

using System.Collections.Generic;
using System.Text;
using Microsoft.ServiceFabric.Data;

namespace Microsoft.AzureCat.Samples.WorkerActorService
{
    using System;
    using System.Fabric;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AzureCat.Samples.Entities;
    using Microsoft.AzureCat.Samples.Framework;
    using Microsoft.AzureCat.Samples.Framework.Interfaces;
    using Microsoft.AzureCat.Samples.WorkerActorService.Interfaces;
    using Microsoft.ServiceFabric.Actors;
    using Microsoft.ServiceFabric.Actors.Client;
    using Microsoft.ServiceFabric.Actors.Runtime;

    /// <remarks>
    /// This actor can be used to start, stop and monitor long running processes.
    /// </remarks>
    [ActorService(Name = "ProcessorActorService")]
    [StatePersistence(StatePersistence.Persisted)]
    internal class ProcessorActor : Actor, IProcessorActor, IRemindable
    {
        #region Private Constants
        //************************************
        // Constants
        //************************************
        private const string DelayProperty = "delay";
        private const string StepsProperty = "steps";
        private const string WorkerId = "workerId";
        private const string Message = "message";

        #endregion

        #region Private Fields

        private Uri queueActorServiceUri;
        private Uri workerActorServiceUri;

        #endregion

        #region IRemindable Methods
        public async Task ReceiveReminderAsync(string reminderName, byte[] context, TimeSpan dueTime, TimeSpan period)
        {
            try
            {
                // Gets the message id from the context
                var messageId = Id.GetStringId();

                // Retrieves the worker id
                ConditionalValue<string> workerIdResult = await this.StateManager.TryGetStateAsync<string>(WorkerId);

                // Retrieves the message
                ConditionalValue<Message> messageResult = await this.StateManager.TryGetStateAsync<Message>(Message);

                // Retrieves the cancellation token source from the actor state
                ConditionalValue<CancellationToken> cancellationTokenResult = await this.StateManager.TryGetStateAsync<CancellationToken>(messageId);

                if (workerIdResult.HasValue &&
                    messageResult.HasValue &&
                    cancellationTokenResult.HasValue)
                {
                    string workerId = workerIdResult.Value;
                    Message message = messageResult.Value;
                    CancellationToken cancellationToken = cancellationTokenResult.Value;
                    
                    // Tries to start the processor. If the processor is already running, the task will timeout after 1 second.
                    List<Task> taskList = new List<Task>
                    {
                        InternalProcessParallelMessageAsync(workerId, message, cancellationToken),
                        Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)
                    };
                    await Task.WhenAny(taskList);
                }
            }
            catch (Exception ex)
            {
                ActorEventSource.Current.Error(ex);
            }
        } 
        #endregion

        #region IProcessorActor Methods

        /// <summary>
        /// Starts processing messages from the work queue in a sequential order.
        /// </summary>
        /// <param name="cancellationToken">This CancellationToken is used to stop message processing.</param>
        /// <returns></returns>
        public async Task ProcessSequentialMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                Message message;

                // Creates the proxy to call the queue actor
                ICircularQueueActor queueActorProxy = ActorProxy.Create<ICircularQueueActor>(new ActorId(this.Id.ToString()), this.queueActorServiceUri);

                // Creates the proxy to call the worker actor
                IWorkerActor workerActorProxy = ActorProxy.Create<IWorkerActor>(new ActorId(this.Id.ToString()), this.workerActorServiceUri);

                // The method keeps processing messages from the queue, until the queue is empty
                while ((message = await queueActorProxy.DequeueAsync()) != null)
                {
                    try
                    {
                        // Message validation
                        if (string.IsNullOrWhiteSpace(message.MessageId) ||
                            string.IsNullOrWhiteSpace(message.Body))
                        {
                            ActorEventSource.Current.Message("Message Invalid.");
                            continue;
                        }

                        // Create delay variable and assign 1 second as default value
                        TimeSpan delay = TimeSpan.FromSeconds(1);

                        // Create steps variable and assign 10 as default value
                        int steps = 10;

                        if (message.Properties != null)
                        {
                            // Checks if the message Properties collection contains the delay property
                            if (message.Properties.ContainsKey(DelayProperty))
                            {
                                if (message.Properties[DelayProperty] is TimeSpan)
                                {
                                    // Assigns the property value to the delay variable
                                    delay = (TimeSpan) message.Properties[DelayProperty];
                                }
                                else
                                {
                                    string value = message.Properties[DelayProperty] as string;
                                    if (value != null)
                                    {
                                        TimeSpan temp;
                                        if (TimeSpan.TryParse(value, out temp))
                                        {
                                            delay = temp;
                                        }
                                    }
                                }
                            }

                            // Checks if the message Properties collection contains the steps property
                            if (message.Properties.ContainsKey(StepsProperty))
                            {
                                if (message.Properties[StepsProperty] is int)
                                {
                                    // Assigns the property value to the steps variable
                                    steps = (int) message.Properties[StepsProperty];
                                }
                                if (message.Properties[StepsProperty] is long)
                                {
                                    // Assigns the property value to the steps variable
                                    steps = (int) (long) message.Properties[StepsProperty];
                                }
                                else
                                {
                                    string value = message.Properties[StepsProperty] as string;
                                    if (value != null)
                                    {
                                        int temp;
                                        if (int.TryParse(value, out temp))
                                        {
                                            steps = temp;
                                        }
                                    }
                                }
                            }
                        }

                        // NOTE!!!! This section should be replaced by some real computation
                        for (int i = 0; i < steps; i++)
                        {
                            ActorEventSource.Current.Message($"MessageId=[{message.MessageId}] Body=[{message.Body}] ProcessStep=[{i + 1}]");
                            try
                            {
                                await Task.Delay(delay, cancellationToken);
                            }
                            catch (TaskCanceledException)
                            {
                            }

                            if (!cancellationToken.IsCancellationRequested)
                            {
                                continue;
                            }
                            // NOTE: If message processing has been cancelled, 
                            // the method returns immediately without any result
                            ActorEventSource.Current.Message($"MessageId=[{message.MessageId}] elaboration has been canceled and sequential message processing stopped.");
                            return;
                        }
                        ActorEventSource.Current.Message($"MessageId=[{message.MessageId}] has been successfully processed.");

                        for (int n = 1; n <= 3; n++)
                        {
                            try
                            {
                                // Simulates a return value between 1 and 100
                                Random random = new Random();
                                int returnValue = random.Next(1, 101);

                                // Returns result to worker actor
                                await workerActorProxy.ReturnSequentialProcessingAsync(message.MessageId, returnValue);

                                //Logs event
                                ActorEventSource.Current.Message($"Sequential processing of MessageId=[{message.MessageId}] ReturnValue=[{returnValue}] successfully returned.");
                                break;
                            }
                            catch (FabricTransientException ex)
                            {
                                ActorEventSource.Current.Message(ex.Message);
                            }
                            catch (AggregateException ex)
                            {
                                foreach (Exception e in ex.InnerExceptions)
                                {
                                    ActorEventSource.Current.Message(e.Message);
                                }
                            }
                            catch (Exception ex)
                            {
                                ActorEventSource.Current.Message(ex.Message);
                            }
                            Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).Wait(cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        ActorEventSource.Current.Error(ex);
                    }
                }

                for (int n = 1; n <= 3; n++)
                {
                    try
                    {
                        // Sets the sequential processing state to false
                        await workerActorProxy.CloseSequentialProcessingAsync(false);
                        ActorEventSource.Current.Message("Closed sequential processing.");
                        return;
                    }
                    catch (FabricTransientException ex)
                    {
                        ActorEventSource.Current.Message(ex.Message);
                    }
                    catch (AggregateException ex)
                    {
                        foreach (Exception e in ex.InnerExceptions)
                        {
                            ActorEventSource.Current.Message(e.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        ActorEventSource.Current.Message(ex.Message);
                        throw;
                    }
                    Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).Wait(cancellationToken);
                }
                throw new TimeoutException();
            }
            catch (Exception ex)
            {
                ActorEventSource.Current.Error(ex);
            }
        }

        public async Task ProcessParallelMessagesAsync(string workerId, Message message, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workerId) || message == null)
                {
                    return;
                }

                await this.StateManager.TryAddStateAsync(WorkerId, 
                                                         workerId, 
                                                         cancellationToken);

                await this.StateManager.TryAddStateAsync(Message,
                                                         message,
                                                         cancellationToken);

                await this.StateManager.TryAddStateAsync(this.Id.GetStringId(),
                                                         cancellationToken,
                                                         cancellationToken);

                await this.RegisterReminderAsync(
                    Guid.NewGuid().ToString(),
                    null,
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(-1));
            }
            catch (Exception ex)
            {
                ActorEventSource.Current.Error(ex);
            }
        }

        #endregion

        #region Protected Virtual Methods

        /// <summary>
        /// Process a messages.
        /// </summary>
        /// <param name="workerId">The worker id.</param>
        /// <param name="message">The message to process</param>
        /// <param name="cancellationToken">The cancellation token to interrupt message processing.</param>
        /// <returns>The object at the beginning of the circular queue.</returns>
        protected virtual async Task InternalProcessParallelMessageAsync(string workerId, Message message, CancellationToken cancellationToken)
        {
            try
            {
                // Message validation
                if (string.IsNullOrWhiteSpace(message.MessageId) ||
                    string.IsNullOrWhiteSpace(message.Body))
                {
                    return;
                }

                // Create delay variable and assign 1 second as default value
                TimeSpan delay = TimeSpan.FromSeconds(1);

                // Create steps variable and assign 10 as default value
                int steps = 10;

                if (message.Properties != null)
                {
                    // Checks if the message Properties collection contains the delay property
                    if (message.Properties.ContainsKey(DelayProperty))
                    {
                        if (message.Properties[DelayProperty] is TimeSpan)
                        {
                            // Assigns the property value to the delay variable
                            delay = (TimeSpan)message.Properties[DelayProperty];
                        }
                        else
                        {
                            string value = message.Properties[DelayProperty] as string;
                            if (value != null)
                            {
                                TimeSpan temp;
                                if (TimeSpan.TryParse(value, out temp))
                                {
                                    delay = temp;
                                }
                            }
                        }
                    }

                    // Checks if the message Properties collection contains the steps property
                    if (message.Properties.ContainsKey(StepsProperty))
                    {
                        if (message.Properties[StepsProperty] is int)
                        {
                            // Assigns the property value to the steps variable
                            steps = (int)message.Properties[StepsProperty];
                        }
                        if (message.Properties[StepsProperty] is long)
                        {
                            // Assigns the property value to the steps variable
                            steps = (int)(long)message.Properties[StepsProperty];
                        }
                        else
                        {
                            string value = message.Properties[StepsProperty] as string;
                            if (value != null)
                            {
                                int temp;
                                if (int.TryParse(value, out temp))
                                {
                                    steps = temp;
                                }
                            }
                        }
                    }
                }

                // NOTE!!!! This section should be replaced by some real computation
                for (int i = 0; i < steps; i++)
                {
                    ActorEventSource.Current.Message($"MessageId=[{message.MessageId}] Body=[{message.Body}] ProcessStep=[{i + 1}]");
                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }
                    // NOTE: If message processing has been cancelled, 
                    // the method returns immediately without any result
                    ActorEventSource.Current.Message($"MessageId=[{message.MessageId}] elaboration has been canceled and parallel message processing stopped.");
                    return;
                }
                ActorEventSource.Current.Message($"MessageId=[{message.MessageId}] has been successfully processed.");

                IWorkerActor workerActorProxy = ActorProxy.Create<IWorkerActor>(new ActorId(workerId), this.workerActorServiceUri);

                for (int n = 1; n <= 10; n++)
                {
                    try
                    {
                        // Simulates a return value between 1 and 100
                        Random random = new Random();
                        int returnValue = random.Next(1, 101);

                        // Stops the current processing task: it removes the corresponding state from the worker actor
                        bool ok = await workerActorProxy.ReturnParallelProcessingAsync(message.MessageId, returnValue);
                        if (ok)
                        {
                            ActorEventSource.Current.Message($"Parallel processing of MessageId=[{message.MessageId}] successfully stopped.");
                        }
                        return;
                    }
                    catch (FabricTransientException ex)
                    {
                        ActorEventSource.Current.Message(ex.Message);
                    }
                    catch (AggregateException ex)
                    {
                        foreach (Exception e in ex.InnerExceptions)
                        {
                            ActorEventSource.Current.Message(e.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        ActorEventSource.Current.Message(ex.Message);
                        throw;
                    }
                    Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).Wait(cancellationToken);
                }
                throw new TimeoutException();
            }
            catch (Exception ex)
            {
                ActorEventSource.Current.Error(ex);
            }
        }
        #endregion

        #region Actor Overridden Methods

        protected override Task OnActivateAsync()
        {
            base.OnActivateAsync();
            this.queueActorServiceUri = new Uri($"{this.ApplicationName}/QueueActorService");
            this.workerActorServiceUri = new Uri($"{this.ApplicationName}/WorkerActorService");
            ActorEventSource.Current.Message($"Worker Actor [{this.Id}] activated.");
            return Task.FromResult(0);
        }

        #endregion
    }
}