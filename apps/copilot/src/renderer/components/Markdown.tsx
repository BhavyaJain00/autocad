/** Assistant text rendered as markdown: no raw HTML, links open in the default browser. */
import { memo, useMemo } from 'react';
import type { MouseEvent } from 'react';
import ReactMarkdown from 'react-markdown';
import type { Components } from 'react-markdown';
import { safeExternalUrl } from '../lib/links';
import { useBridge } from './BridgeContext';

interface MarkdownProps {
  text: string;
}

/**
 * Renders markdown with react-markdown (which never renders raw HTML). Links never navigate the
 * window: web links are handed to the main process, everything else is inert. Remote images are
 * not fetched; they appear as a link to the image.
 */
export const Markdown = memo(function Markdown({ text }: MarkdownProps) {
  const bridge = useBridge();

  const components = useMemo<Components>(() => {
    const open = (event: MouseEvent, href: string | undefined): void => {
      event.preventDefault();
      const url = safeExternalUrl(href);
      if (url !== null) bridge.openExternal(url);
    };
    return {
      a({ href, children }) {
        const url = safeExternalUrl(href);
        if (url === null) return <span>{children}</span>;
        return (
          <a href={url} title={url} onClick={(event) => open(event, url)} onAuxClick={(event) => open(event, url)}>
            {children}
          </a>
        );
      },
      img({ src, alt }) {
        const label = alt && alt.trim().length > 0 ? alt : 'image';
        const url = safeExternalUrl(typeof src === 'string' ? src : undefined);
        if (url === null) return <span>[{label}]</span>;
        return (
          <a href={url} title={url} onClick={(event) => open(event, url)} onAuxClick={(event) => open(event, url)}>
            [{label}]
          </a>
        );
      },
      pre({ children }) {
        return <pre tabIndex={0}>{children}</pre>;
      },
    };
  }, [bridge]);

  return (
    <div className="markdown selectable">
      <ReactMarkdown components={components} skipHtml>
        {text}
      </ReactMarkdown>
    </div>
  );
});
