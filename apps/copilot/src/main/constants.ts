/** Application constants shared by CAD Copilot's main-process modules. No Electron imports. */

export const APP_NAME = 'CAD Copilot';

/** Windows AppUserModelID (taskbar grouping, notifications). */
export const APP_USER_MODEL_ID = 'com.cadstudio.copilot';

/** Window background shown before the renderer paints; also the colour of the title-bar overlay. */
export const WINDOW_BACKGROUND_COLOR = '#0d0e10';

/** Colour of the native minimise / close glyphs drawn over the renderer's header. */
export const TITLE_BAR_SYMBOL_COLOR = '#c9ced6';

/** Height (DIP) of the native title-bar overlay; the renderer's draggable header matches it. */
export const TITLE_BAR_HEIGHT = 40;

/** Initial window height (DIP); the width comes from the settings. */
export const WINDOW_DEFAULT_HEIGHT = 820;

/** Smallest window height (DIP). */
export const WINDOW_MIN_HEIGHT = 520;

/** How often the adverts folder is polled for running CAD applications. */
export const CONNECTOR_POLL_INTERVAL_MS = 2_000;

/** Quiet time after a document notification before the connector is asked for fresh state. */
export const DOCUMENT_REFRESH_DEBOUNCE_MS = 150;

/** Quiet time after the user stops resizing the window before the new width is applied. */
export const WINDOW_RESIZE_DEBOUNCE_MS = 200;

/** Longest message text accepted from the renderer (characters). */
export const MESSAGE_MAX_LENGTH = 100_000;

/** Overrides the userData folder (settings, credentials); used by end-to-end tests. */
export const ENV_USER_DATA = 'CADCOPILOT_USER_DATA';

/** Advert id of the connector to attach to (set by a CAD application that starts the copilot). */
export const ENV_CONNECT = 'CADCOPILOT_CONNECT';

/** '1' marks an end-to-end test run (enables the scripted test model when a script is given). */
export const ENV_E2E = 'CADCOPILOT_E2E';

/** Path of a JSON script for the scripted test model (honoured only together with ENV_E2E=1). */
export const ENV_SCRIPT = 'CADCOPILOT_SCRIPT';

/** Anthropic API key from the environment; used when no key is stored. */
export const ENV_API_KEY = 'ANTHROPIC_API_KEY';
