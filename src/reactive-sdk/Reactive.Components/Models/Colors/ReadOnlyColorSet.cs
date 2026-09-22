using System;
using JetBrains.Annotations;
using UnityEngine;

namespace Reactive.Components {
    [PublicAPI]
    public struct ReadOnlyColorSet : IColorSet {
        public Color NotInteractableColor;
        public Color Color;
        public Color ActiveColor;
        public Color HoveredColor;

        // Value-type snapshots do not publish change notifications.
        public event Action? SetUpdatedEvent {
            add { }
            remove { }
        }
        
        public Color GetColor(GraphicState state) {
            if (state.IsHovered()) {
                return state.IsActive() ? ActiveColor : HoveredColor;
            }
            if (state.IsActive()) {
                return ActiveColor;
            }
            if (!state.IsInteractable()) {
                return NotInteractableColor;
            }
            return Color;
        }
    }
}
