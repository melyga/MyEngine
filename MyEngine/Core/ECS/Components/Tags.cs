#nullable enable

namespace MyEngine.Core.ECS.Components;

public struct PlayerTag { }

public struct ViewmodelTag { }

public struct CastsShadowTag { }

public struct StaticGeometryTag { }

public struct TransparentTag { }

public struct UIEntityTag { }

// Назначение:   Маркерные теги для фильтрации сущностей в ECS-запросах
// Зависит от:   —
// Используется: MyEngine.Core системы рендеринга, физики, UI и игровой логики
