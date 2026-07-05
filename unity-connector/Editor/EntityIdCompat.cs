namespace UnityCliBridge
{
    /// <summary>
    /// Unity 6000.3+ 의 InstanceID -> EntityId API 전환 호환 셔틀.
    /// 브리지 프로토콜은 정수 ID 를 유지하고, 내부에서만 신규 API 로 매핑한다.
    /// 같은 세션 내에서 <see cref="GetStableId"/> 로 내보낸 값을
    /// <see cref="IdToObject"/> 로 되돌리는 왕복이 항상 성립한다.
    /// </summary>
    internal static class EntityIdCompat
    {
        /// <summary>
        /// 오브젝트의 정수 기반 안정 ID 를 반환한다.
        /// (6000.5+: EntityId.ToULong, 6000.3: EntityId 캐스트, 이전: GetInstanceID)
        /// </summary>
        public static long GetStableId(this UnityEngine.Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            return (long)UnityEngine.EntityId.ToULong(obj.GetEntityId());
#elif UNITY_6000_3_OR_NEWER
            return (int)obj.GetEntityId();
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>
        /// 정수 ID 로 오브젝트를 찾는다.
        /// (6000.5+: EntityId.FromULong, 6000.3: EntityId 캐스트, 이전: InstanceIDToObject)
        /// </summary>
        public static UnityEngine.Object IdToObject(long id)
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEditor.EditorUtility.EntityIdToObject(UnityEngine.EntityId.FromULong((ulong)id));
#elif UNITY_6000_3_OR_NEWER
            return UnityEditor.EditorUtility.EntityIdToObject((UnityEngine.EntityId)(int)id);
#else
            return UnityEditor.EditorUtility.InstanceIDToObject((int)id);
#endif
        }
    }
}
