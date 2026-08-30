let backtrackWs = null;
let isBacktrackConnected = false;

let backtrackState = {
  obs_connected: false,
  is_recording: false,
  preferred_clip_length_seconds: 60,
  record_duration_ms: 0,
  main_record_start_time_ms: 0,
  replay_buffers: [],
  record_sources: []
};

function connectBacktrackWs() {
  if (backtrackWs) {
    try { backtrackWs.close(); } catch (e) {}
  }

  try {
    backtrackWs = new WebSocket('ws://127.0.0.1:44558');
  } catch (e) {
    handleBacktrackDisconnect();
    return;
  }

  backtrackWs.onopen = () => {
    isBacktrackConnected = true;
    renderAllKeys(true);
    requestStateSnapshot();
  };

  backtrackWs.onmessage = (event) => {
    try {
      const msg = JSON.parse(event.data);
      if (msg.event === 'state_snapshot') {
        const snap = msg.data;
        if (snap) {
          backtrackState.obs_connected = !!snap.obs_connected;
          backtrackState.is_recording = !!snap.is_recording;
          backtrackState.is_main_recording = !!snap.is_main_recording;
          backtrackState.preferred_clip_length_seconds = snap.preferred_clip_length_seconds || 60;
          backtrackState.record_duration_ms = snap.record_duration_ms || 0;
          backtrackState.main_record_start_time_ms = snap.main_record_start_time_ms || 0;
          backtrackState.replay_buffers = snap.replay_buffers || [];
          backtrackState.record_sources = snap.record_sources || [];
        }
        renderAllKeys(false);
      } else if (msg.event === 'replay_saving') {
        triggerSavingAnimation(msg.data && msg.data.source);
      } else if (msg.event === 'replay_saved') {
        triggerSavedAnimation(msg.data && (msg.data.key || msg.data.path));
      } else if (msg.event === 'bookmark_added') {
        triggerBookmarkAnimation();
      }
    } catch (e) {}
  };

  backtrackWs.onerror = () => {
    handleBacktrackDisconnect();
  };

  backtrackWs.onclose = () => {
    handleBacktrackDisconnect();
    setTimeout(connectBacktrackWs, 2000);
  };
}

function handleBacktrackDisconnect() {
  isBacktrackConnected = false;
  backtrackState.obs_connected = false;
  backtrackState.is_recording = false;
  backtrackState.is_main_recording = false;
  backtrackState.record_duration_ms = 0;
  backtrackState.main_record_start_time_ms = 0;
  renderAllKeys(true);
}

function requestStateSnapshot() {
  if (backtrackWs && backtrackWs.readyState === WebSocket.OPEN) {
    backtrackWs.send(JSON.stringify({ action: 'get_state' }));
  }
}
