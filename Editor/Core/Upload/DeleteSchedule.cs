using System;
using System.Collections.Generic;
using UnityEngine;

namespace ContentRepo.Editor
{
    [Serializable]
    public sealed class DeleteScheduleEntry
    {
        public string buildId;
        public string generation;
        public string contentPackage;
        public string platform;
        public string markedAt;    // ISO 8601 UTC
        public string deleteAfter; // ISO 8601 UTC
        public string markedBy;    // machine name or user hint

        public bool IsDue => DateTime.TryParse(deleteAfter, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) &&
            DateTime.UtcNow >= dt;
    }

    [Serializable]
    public sealed class DeleteSchedule
    {
        public List<DeleteScheduleEntry> entries = new();

        public void Add(DeleteScheduleEntry entry) => entries.Add(entry);

        public bool Remove(string buildId, string generation)
        {
            var count = entries.RemoveAll(e =>
                string.Equals(e.buildId, buildId, StringComparison.Ordinal) &&
                string.Equals(e.generation, generation, StringComparison.Ordinal));
            return count > 0;
        }

        public bool Contains(string buildId, string generation) =>
            entries.Exists(e =>
                string.Equals(e.buildId, buildId, StringComparison.Ordinal) &&
                string.Equals(e.generation, generation, StringComparison.Ordinal));

        public List<DeleteScheduleEntry> DueEntries()
        {
            var due = new List<DeleteScheduleEntry>();
            foreach (var e in entries)
                if (e.IsDue) due.Add(e);
            return due;
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static DeleteSchedule FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new DeleteSchedule();
            try { return JsonUtility.FromJson<DeleteSchedule>(json) ?? new DeleteSchedule(); }
            catch { return new DeleteSchedule(); }
        }
    }
}
