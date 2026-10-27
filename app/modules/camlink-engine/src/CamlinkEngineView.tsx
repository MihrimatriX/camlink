import { requireNativeView } from 'expo';
import * as React from 'react';
import type { CamlinkPreviewProps } from './CamlinkEngine.types';

const NativeView: React.ComponentType<CamlinkPreviewProps> = requireNativeView('CamlinkEngine');

/** Kamera önizlemesi; OBS'e giden görüntünün aynısını gösterir. */
export default function CamlinkPreview(props: CamlinkPreviewProps) {
  return <NativeView {...props} />;
}
