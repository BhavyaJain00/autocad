/** Gives every component access to the main-process bridge without prop drilling. */
import { createContext, useContext } from 'react';
import type { ReactNode } from 'react';
import type { CopilotBridge } from '../../shared/bridge';

const BridgeContext = createContext<CopilotBridge | null>(null);

export function BridgeProvider({ bridge, children }: { bridge: CopilotBridge; children: ReactNode }) {
  return <BridgeContext.Provider value={bridge}>{children}</BridgeContext.Provider>;
}

/** The `window.copilot` bridge. Only valid below a {@link BridgeProvider}. */
export function useBridge(): CopilotBridge {
  const bridge = useContext(BridgeContext);
  if (bridge === null) throw new Error('useBridge must be used inside <BridgeProvider>');
  return bridge;
}
