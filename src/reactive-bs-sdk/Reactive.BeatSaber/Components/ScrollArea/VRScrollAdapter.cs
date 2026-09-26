using Reactive.Components;
using UnityEngine;
using UnityEngine.EventSystems;

#if !COMPILE_EDITOR

namespace Reactive.BeatSaber.Components {
    [DefaultExecutionOrder(-1000)]
    [RequireComponent(typeof(PointerEventsHandler))]
    internal class VRScrollAdapter : MonoBehaviour {
        private PointerEventsHandler _pointerEventsHandler = null!;
        private IVRPlatformHelper _platformHelper = null!;
        private PointerEventData? _pointerEventData;
        private EventSystem? _eventSystem;
        private float _multiplier;

        private void Awake() {
            _pointerEventsHandler = GetComponent<PointerEventsHandler>();
            var context = BeatSaberUtils.MenuContainer;
            _platformHelper = context.Resolve<IVRPlatformHelper>();
            _multiplier = BeatSaberUtils.UsesFPFC ? -10f : -1f;
        }

        private void Update() {
            if (!_pointerEventsHandler.IsHovered) {
                return;
            }
            var delta = _platformHelper.GetAnyJoystickMaxAxis();
            if (Mathf.Approximately(delta.x, 0f) && Mathf.Approximately(delta.y, 0f)) {
                return;
            }
            if (_pointerEventData == null || _eventSystem != EventSystem.current) {
                _eventSystem = EventSystem.current;
                _pointerEventData = new PointerEventData(_eventSystem);
            }
            _pointerEventData.scrollDelta = delta * _multiplier;
            _pointerEventsHandler.OnScroll(_pointerEventData);
        }
    }
}

#endif
