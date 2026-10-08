using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using SoundCalcs.IO;

namespace SoundCalcs.Revit
{
    /// <summary>
    /// ExternalEvent-based dispatcher for marshaling actions back to the Revit API thread.
    /// Use this to run Revit API calls after background tasks complete.
    /// </summary>
    public class RevitApiDispatcher
    {
        private readonly ExternalEvent _externalEvent;
        private readonly DelegateHandler _handler;

        public RevitApiDispatcher()
        {
            _handler = new DelegateHandler();
            _externalEvent = ExternalEvent.Create(_handler);
        }

        /// <summary>
        /// Queue an action to run on the Revit API thread, after any queued before it.
        /// The action receives the UIApplication for API calls.
        /// </summary>
        public void Enqueue(Action<UIApplication> action)
        {
            _handler.SetAction(action);
            _externalEvent.Raise();
        }

        private class DelegateHandler : IExternalEventHandler
        {
            // Every queued action runs: two speakers aimed before Revit gets to the event both get stored
            private readonly Queue<Action<UIApplication>> _pending = new Queue<Action<UIApplication>>();
            private readonly object _lock = new object();

            public void SetAction(Action<UIApplication> action)
            {
                lock (_lock)
                {
                    _pending.Enqueue(action);
                }
            }

            public void Execute(UIApplication app)
            {
                while (true)
                {
                    Action<UIApplication> action;
                    lock (_lock)
                    {
                        if (_pending.Count == 0) return;
                        action = _pending.Dequeue();
                    }

                    try
                    {
                        action(app);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"RevitApiDispatcher action failed: {ex}");
                    }
                }
            }

            public string GetName() => "SoundCalcs.RevitApiDispatcher";
        }
    }
}
