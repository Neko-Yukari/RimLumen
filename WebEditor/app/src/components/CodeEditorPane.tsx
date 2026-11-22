import { useEffect, useRef } from 'react';
import { EditorView, basicSetup } from 'codemirror';
import { Compartment, EditorState } from '@codemirror/state';
import { json } from '@codemirror/lang-json';
import { xml } from '@codemirror/lang-xml';
import { linter, forceLinting, type Diagnostic } from '@codemirror/lint';
import type { AssetFormat, ValidationResult } from '../wasm/types';

interface CodeEditorPaneProps {
  value: string;
  format: AssetFormat;
  docRevision: number;
  onValidate: (source: string, format: AssetFormat) => ValidationResult;
  onValidChange: (source: string, format: AssetFormat, result: ValidationResult) => void;
  onValidationError: (result: ValidationResult) => void;
  onUserEdit: (nextText: string) => void;
}

const editorTheme = EditorView.theme(
  {
    '&': {
      backgroundColor: '#0D1015',
      color: '#E6ECF5',
      height: '100%',
      fontSize: '13px',
      fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace",
    },
    '.cm-scroller': {
      overflow: 'auto',
      lineHeight: '1.55',
    },
    '.cm-content': {
      caretColor: '#E5A93C',
      padding: '10px 0',
    },
    '.cm-gutters': {
      backgroundColor: '#12161E',
      color: '#67748A',
      borderRight: '1px solid #2B3240',
    },
    '.cm-activeLineGutter': {
      backgroundColor: '#1B212E',
      color: '#E5A93C',
    },
    '.cm-activeLine': {
      backgroundColor: '#171C26',
    },
    '.cm-selectionBackground, ::selection': {
      backgroundColor: 'rgba(229, 169, 60, 0.24) !important',
    },
    '&.cm-focused .cm-cursor': {
      borderLeftColor: '#E5A93C',
    },
    '.cm-lintRange-error': {
      backgroundImage: 'none',
      textDecoration: 'underline wavy #F06464',
    },
    '.cm-tooltip.cm-tooltip-lint': {
      backgroundColor: '#1D222D',
      border: '1px solid #2B3240',
      color: '#E6ECF5',
    },
  },
  { dark: true },
);

export function CodeEditorPane({
  value,
  format,
  docRevision,
  onValidate,
  onValidChange,
  onValidationError,
  onUserEdit,
}: CodeEditorPaneProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);
  const langCompartmentRef = useRef(new Compartment());

  const formatRef = useRef<AssetFormat>(format);
  formatRef.current = format;
  const suppressNextUserEditRef = useRef(false);

  const callbacksRef = useRef({ onValidate, onValidChange, onValidationError, onUserEdit });
  callbacksRef.current = { onValidate, onValidChange, onValidationError, onUserEdit };

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    const wasmLinter = linter(
      (view): Diagnostic[] => {
        const docText = view.state.doc.toString();
        const activeFormat = formatRef.current;
        const result = callbacksRef.current.onValidate(docText, activeFormat);

        if (result.ok) {
          callbacksRef.current.onValidChange(docText, activeFormat, result);
          return [];
        }

        callbacksRef.current.onValidationError(result);
        const lineNum = Math.min(Math.max(1, result.line ?? 1), view.state.doc.lines);
        const line = view.state.doc.line(lineNum);
        const colOffset = Math.max(0, (result.column ?? 1) - 1);
        const from = Math.min(line.from + colOffset, line.to);
        const to = Math.max(from, line.to);
        return [
          {
            from,
            to,
            severity: 'error',
            message: result.error ?? '解析或校验失败',
          },
        ];
      },
      { delay: 140 },
    );

    const state = EditorState.create({
      doc: value,
      extensions: [
        basicSetup,
        langCompartmentRef.current.of(format === 'xml' ? xml() : json()),
        editorTheme,
        wasmLinter,
        EditorView.contentAttributes.of({ 'aria-label': '特效资产代码编辑器' }),
        EditorView.updateListener.of((update) => {
          if (!update.docChanged) return;
          if (suppressNextUserEditRef.current) {
            suppressNextUserEditRef.current = false;
            return;
          }
          callbacksRef.current.onUserEdit(update.state.doc.toString());
        }),
      ],
    });

    const view = new EditorView({ state, parent: host });
    viewRef.current = view;
    forceLinting(view);

    return () => {
      viewRef.current = null;
      view.destroy();
    };
    // Mount editor once; docRevision and format effects keep state synced cleanly without losing focus while typing.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const view = viewRef.current;
    if (!view) return;
    view.dispatch({
      effects: langCompartmentRef.current.reconfigure(format === 'xml' ? xml() : json()),
    });
    forceLinting(view);
  }, [format]);

  useEffect(() => {
    const view = viewRef.current;
    if (!view) return;
    const current = view.state.doc.toString();
    if (current !== value) {
      suppressNextUserEditRef.current = true;
      view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: value },
      });
    }
    forceLinting(view);
    // Only reset editor content when parent loads a preset/file/cross-format doc (tracked by docRevision).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [docRevision]);

  return <div ref={hostRef} className="code-editor-host" />;
}
