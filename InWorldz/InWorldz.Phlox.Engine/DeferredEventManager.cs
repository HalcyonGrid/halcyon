/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenMetaverse;

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// Holds events for scripts that are not yet loaded, until they are loaded up
    /// at which time all the deffered messages are posted to the script
    /// </summary>
    internal class DeferredEventManager
    {
        private const int EXPIRATION_SECONDS = 60;
        private const int MAX_DEFERRED_EVENTS = 32;

        public class DefferredEvents
        {
            public IList<VM.PostedEvent> EventList;
            public IList<EnableDisableFlag> EnableDisableList;
            public IList<UUID> GroupCrossedAvatarsReadyList;
        }

        private class DeferredEventList : IComparable<DeferredEventList>
        {
            public UUID ItemId;
            public ulong ExpiresOn;
            
            public List<VM.PostedEvent> EvtList = new List<VM.PostedEvent>();
            public List<EnableDisableFlag> EnableDisableList = new List<EnableDisableFlag>();
            public List<UUID> GroupCrossedAvatarsReadyList = new List<UUID>();

            public C5.IPriorityQueueHandle<DeferredEventList> Handle;

            public DeferredEventList(UUID itemId)
            {
                ItemId = itemId;
                ExpiresOn = Util.Clock.GetLongTickCount() + (EXPIRATION_SECONDS * 1000);
            }

            #region IComparable<DeferredEventList> Members

            public int CompareTo(DeferredEventList other)
            {
                return ExpiresOn.CompareTo(other.ExpiresOn);
            }

            #endregion
        }

        /// <summary>
        /// Deferred events sorted by expiration time
        /// </summary>
        private C5.IntervalHeap<DeferredEventList> _sortedEvents =
            new C5.IntervalHeap<DeferredEventList>();


        /// <summary>
        /// Deferred events indexed by script item id
        /// </summary>
        private Dictionary<UUID, DeferredEventList> _eventsByScript =
            new Dictionary<UUID, DeferredEventList>();

        /// <summary>
        /// Add a new deferred event
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="eventInfo"></param>
        public void AddEvent(UUID itemId, VM.PostedEvent eventInfo)
        {
            DeferredEventList eventList;
            if (_eventsByScript.TryGetValue(itemId, out eventList))
            {
                if (eventList.EvtList.Count < MAX_DEFERRED_EVENTS)
                {
                    eventList.EvtList.Add(eventInfo);
                }
            }
            else
            {
                eventList = new DeferredEventList(itemId);
                eventList.EvtList.Add(eventInfo);
                _eventsByScript.Add(itemId, eventList);

                C5.IPriorityQueueHandle<DeferredEventList> handle = null;
                _sortedEvents.Add(ref handle, eventList);
                eventList.Handle = handle;
            }
        }

        /// <summary>
        /// Finds all events related to a given script
        /// </summary>
        /// <param name="itemId"></param>
        /// <returns></returns>
        public DeferredEventManager.DefferredEvents FindEvents(UUID itemId)
        {
            DeferredEventList eventList;
            if (_eventsByScript.TryGetValue(itemId, out eventList))
            {
                Delete(eventList);
                return new DefferredEvents 
                { 
                    EventList = eventList.EvtList, 
                    EnableDisableList = eventList.EnableDisableList,
                    GroupCrossedAvatarsReadyList = eventList.GroupCrossedAvatarsReadyList 
                };
            }
            else
            {
                return null;
            }
        }

        private void Delete(DeferredEventList eventList)
        {
            _sortedEvents.Delete(eventList.Handle);
            _eventsByScript.Remove(eventList.ItemId);
        }

        /// <summary>
        /// Removes expired events
        /// </summary>
        public void DoExpirations()
        {
            while (_sortedEvents.Count > 0)
            {
                DeferredEventList list = _sortedEvents.FindMin();
                if (Util.Clock.GetLongTickCount() >= list.ExpiresOn)
                {
                    Delete(list);
                }
                else
                {
                    break;
                }
            }
        }

        internal void AddEnableDisableEvent(UUID itemId, EnableDisableFlag enableDisableFlag)
        {
            DeferredEventList eventList;
            if (_eventsByScript.TryGetValue(itemId, out eventList))
            {
                eventList.EnableDisableList.Add(enableDisableFlag);
            }
            else
            {
                eventList = new DeferredEventList(itemId);
                eventList.EnableDisableList.Add(enableDisableFlag);
                _eventsByScript.Add(itemId, eventList);

                C5.IPriorityQueueHandle<DeferredEventList> handle = null;
                _sortedEvents.Add(ref handle, eventList);
                eventList.Handle = handle;
            }
        }

        internal void AddGroupCrossedAvatarsReadyEvent(Tuple<UUID,UUID> scriptIdUserId)
        {
            DeferredEventList eventList;
            if (_eventsByScript.TryGetValue(scriptIdUserId.Item1, out eventList))
            {
                eventList.GroupCrossedAvatarsReadyList.Add(scriptIdUserId.Item2);
            }
            else
            {
                eventList = new DeferredEventList(scriptIdUserId.Item1);
                eventList.GroupCrossedAvatarsReadyList.Add(scriptIdUserId.Item2);
                _eventsByScript.Add(scriptIdUserId.Item1, eventList);

                C5.IPriorityQueueHandle<DeferredEventList> handle = null;
                _sortedEvents.Add(ref handle, eventList);
                eventList.Handle = handle;
            }
        }
    }
}
