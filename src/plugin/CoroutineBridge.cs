using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BepinExJS.Plugin
{
    public class CoroutineBridge
    {
        private readonly MonoBehaviour _host;
        private readonly List<Coroutine> _activeCoroutines = new List<Coroutine>();

        public CoroutineBridge(MonoBehaviour host)
        {
            _host = host;
        }

        public Coroutine Start(IEnumerator routine)
        {
            var cr = _host.StartCoroutine(routine);
            lock (_activeCoroutines)
            {
                _activeCoroutines.Add(cr);
            }
            return cr;
        }

        public void WaitSeconds(float seconds, Action onComplete)
        {
            if (onComplete == null) return;
            Start(WaitSecondsRoutine(seconds, onComplete));
        }

        public void WaitNextFrame(Action onComplete)
        {
            if (onComplete == null) return;
            Start(WaitNextFrameRoutine(onComplete));
        }

        public void WaitForFixedUpdate(Action onComplete)
        {
            if (onComplete == null) return;
            Start(WaitForFixedUpdateRoutine(onComplete));
        }

        public void Stop(Coroutine cr)
        {
            if (cr == null) return;
            _host.StopCoroutine(cr);
            lock (_activeCoroutines)
            {
                _activeCoroutines.Remove(cr);
            }
        }

        public void StopAll()
        {
            lock (_activeCoroutines)
            {
                foreach (var cr in _activeCoroutines)
                {
                    try { _host.StopCoroutine(cr); } catch { }
                }
                _activeCoroutines.Clear();
            }
        }

        private IEnumerator WaitSecondsRoutine(float seconds, Action onComplete)
        {
            yield return new WaitForSeconds(seconds);
            try
            {
                onComplete();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Coroutine] Exception in waitSeconds callback: {ex}");
            }
        }

        private IEnumerator WaitNextFrameRoutine(Action onComplete)
        {
            yield return null;
            try
            {
                onComplete();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Coroutine] Exception in waitNextFrame callback: {ex}");
            }
        }

        private IEnumerator WaitForFixedUpdateRoutine(Action onComplete)
        {
            yield return new WaitForFixedUpdate();
            try
            {
                onComplete();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Coroutine] Exception in waitForFixedUpdate callback: {ex}");
            }
        }
    }
}
