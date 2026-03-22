using System;
using System.Diagnostics;
using Autodesk.Revit.UI;

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
        /// Queue an action to run on the Revit API thread.
        /// The action receives the UIApplication for API calls.
        /// </summary>
        public void Enqueue(Action<UIApplication> action)
        {
            _handler.SetAction(action);
            _externalEvent.Raise();
        }

        private class DelegateHandler : IExternalEventHandler
        {
            private Action<UIApplication> _pendingAction;
            private readonly object _lock = new object();

            public void SetAction(Action<UIApplication> action)
            {
                lock (_lock)
                {
                    _pendingAction = action;
                }
            }

            public void Execute(UIApplication app)
            {
                Action<UIApplication> action;
                lock (_lock)
                {
                    action = _pendingAction;
                    _pendingAction = null;
                }

                if (action == null) return;

                try
                {
                    action(app);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SoundCalcs] RevitApiDispatcher action failed: {ex.Message}");
                }
            }

            public string GetName() => "SoundCalcs.RevitApiDispatcher";
        }
    }
}
