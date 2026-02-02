// ============================================================================
// FILE: MainThreadDispatcher.cs
// Dispatches actions to the Unity main thread from background threads.
// Required for SSE streaming which processes data on background threads.
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher instance;
        private static readonly Queue<Action> actionQueue = new Queue<Action>();
        private static readonly object queueLock = new object();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if (instance == null)
            {
                var go = new GameObject("MainThreadDispatcher");
                instance = go.AddComponent<MainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            lock (queueLock)
            {
                while (actionQueue.Count > 0)
                {
                    try
                    {
                        actionQueue.Dequeue()?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[MainThreadDispatcher] Error executing action: {e}");
                    }
                }
            }
        }

        public static void RunOnMainThread(Action action)
        {
            if (action == null) return;

            lock (queueLock)
            {
                actionQueue.Enqueue(action);
            }
        }
    }
}