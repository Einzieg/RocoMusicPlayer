import type { AppState, RpcParams } from '../types.ts';

export function transportAction(state: AppState): 'pause' | 'resume' | 'play' {
  if (state.playback.state === 'paused') return 'resume';
  if (['playing', 'countdown'].includes(state.playback.state)) return 'pause';
  return 'play';
}

// A click means a specific action, not a toggle that can race with focus-loss pause.
export async function runTransport(
  action: ReturnType<typeof transportAction>,
  state: AppState,
  id: string | undefined,
  dependencies: {
    prepareWindow: () => Promise<void>;
    cancelled: () => boolean;
    send: <M extends keyof RpcParams>(method: M, params: RpcParams[M]) => Promise<unknown>;
  },
) {
  if (action === 'pause') {
    await dependencies.send('pause', { paused: true });
    return;
  }
  if (dependencies.cancelled()) return;
  if (action === 'play' && !state.preview && state.settings.backend === 'DD') {
    // Keep the player window in place until DD startup and its Win-key guard finish.
    await dependencies.send('prepare_playback', { id, stop_sequence: state.stop_sequence });
    if (dependencies.cancelled()) return;
  }
  await dependencies.prepareWindow();
  if (dependencies.cancelled()) return;
  const params = { activate_target: !state.preview, stop_sequence: state.stop_sequence };
  if (action === 'resume') await dependencies.send('resume', params);
  else await dependencies.send('play', { ...params, id, preview: state.preview });
}
