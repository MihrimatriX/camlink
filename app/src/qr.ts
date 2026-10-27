/** OBS'in "Bağlantı bilgilerini göster" penceresindeki QR: obsws://IP:PORT/ŞİFRE */
export function parseObsQr(data: string): { host: string; port: number; password: string } | null {
  const m = /^obsws:\/\/([^:/\s]+):(\d+)(?:\/(.*))?$/i.exec(data.trim());
  if (!m) return null;
  let password = m[3] ?? '';
  try {
    password = decodeURIComponent(password);
  } catch {}
  return { host: m[1], port: Number(m[2]), password };
}
