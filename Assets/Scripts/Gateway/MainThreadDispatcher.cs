// ============================================================================
// FILE: MainThreadDispatcher.cs
// Utility to dispatch actions to the Unity main thread from background threads.
// Required for SSE streaming which may process data on a background thread.
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// Dispatches actions to the Unity main thread.
    /// Add this component to a GameObject that persists across scenes.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher instance;
        private static readonly object queueLock = new object();
        private static readonly Queue<Action> actionQueue = new Queue<Action>();

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static MainThreadDispatcher Instance
        {
            get
            {
                if (instance == null)
                {
                    // Try to find existing instance
                    instance = FindFirstObjectByType<MainThreadDispatcher>();

                    if (instance == null)
                    {
                        // Create new instance
                        GameObject go = new GameObject("MainThreadDispatcher");
                        instance = go.AddComponent<MainThreadDispatcher>();
                        DontDestroyOnLoad(go);
                    }
                }
                return instance;
            }
        }

        private void Awake()
        {
            if (instance == null)
            {
                instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (instance != this)
            {
                Destroy(gameObject);
            }
        }

        private void Update()
        {
            // Process queued actions
            while (true)
            {
                Action action = null;
                lock (queueLock)
                {
                    if (actionQueue.Count > 0)
                    {
                        action = actionQueue.Dequeue();
                    }
                }

                if (action == null)
                    break;

                try
                {
                    action.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MainThreadDispatcher] Exception in queued action: {e}");
                }
            }
        }

        /// <summary>
        /// Enqueue an action to be executed on the main thread.
        /// Safe to call from any thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null)
                return;

            lock (queueLock)
            {
                actionQueue.Enqueue(action);
            }
        }

        /// <summary>
        /// Enqueue an action if not already on the main thread.
        /// If on the main thread, executes immediately.
        /// </summary>
        public static void RunOnMainThread(Action action)
        {
            if (action == null)
                return;

            // Check if we're on the main thread
            if (IsMainThread())
            {
                action.Invoke();
            }
            else
            {
                Enqueue(action);
            }
        }

        /// <summary>
        /// Check if the current thread is the main Unity thread.
        /// </summary>
        public static bool IsMainThread()
        {
            return System.Threading.Thread.CurrentThread.ManagedThreadId == mainThreadId;
        }

        private static int mainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }
    }
}
