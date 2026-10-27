/*
 * CamLink OBS eklentisi
 *
 * "CamLink telefon kamerası" kaynağı: telefondaki CamLink uygulamasının SRT ile yolladığı
 * görüntüyü ve sesi alır. İçeride OBS'in kendi Medya Kaynağını (ffmpeg_source) SRT dinleyici
 * olarak çalıştırır; görüntüyü çizer, sesi kendi sesi gibi aktarır.
 *
 * Ayrıca yerel ağa her 2 saniyede bir duyuru yollar (UDP 4467). CamLink bu duyuruyu görüp
 * bilgisayarı ve kamera yuvalarını listeler; kullanıcı IP ya da port yazmak zorunda kalmaz.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <windows.h>
#else
/* Linux sürümü yalnızca geliştirme sırasında gerçek OBS ile test etmek için derlenir. */
#include <arpa/inet.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netinet/in.h>
#include <pthread.h>
#include <sys/socket.h>
#include <unistd.h>
#include <time.h>
#endif

#include <obs-module.h>

#ifndef _WIN32
typedef pthread_mutex_t CRITICAL_SECTION;
#define InitializeCriticalSection(m) pthread_mutex_init((m), NULL)
#define DeleteCriticalSection(m) pthread_mutex_destroy(m)
#define EnterCriticalSection(m) pthread_mutex_lock(m)
#define LeaveCriticalSection(m) pthread_mutex_unlock(m)
#endif

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE("camlink", "en-US")

MODULE_EXPORT const char *obs_module_name(void)
{
	return "CamLink";
}

MODULE_EXPORT const char *obs_module_description(void)
{
	return obs_module_text("CamLink.Description");
}

#define SOURCE_ID "camlink_source"
#define BASE_PORT 9000
#define MAX_CAMS 16
#define BEACON_PORT 4467
#define BEACON_INTERVAL_MS 2000

struct camlink {
	obs_source_t *source;
	obs_source_t *child;
	int port;
	int latency;
};

static CRITICAL_SECTION cams_lock;
static struct camlink *cams[MAX_CAMS];

/* ------------------------------------------------------------------------- */
/* Port yönetimi: her CamLink kaynağı ayrı bir UDP portunu dinler.            */

static bool port_taken(int port, struct camlink *self)
{
	for (int i = 0; i < MAX_CAMS; i++)
		if (cams[i] && cams[i] != self && cams[i]->port == port)
			return true;
	return false;
}

/* İstenen port boşsa onu, değilse 9000'den başlayarak ilk boş portu verir. Kilit tutulurken çağrılır. */
static int pick_port(int wanted, struct camlink *self)
{
	if (wanted >= 1024 && wanted <= 65535 && !port_taken(wanted, self))
		return wanted;
	for (int p = BASE_PORT; p < BASE_PORT + 1000; p++)
		if (!port_taken(p, self))
			return p;
	return BASE_PORT;
}

static void register_cam(struct camlink *c)
{
	for (int i = 0; i < MAX_CAMS; i++) {
		if (!cams[i]) {
			cams[i] = c;
			return;
		}
	}
}

static void unregister_cam(struct camlink *c)
{
	for (int i = 0; i < MAX_CAMS; i++)
		if (cams[i] == c)
			cams[i] = NULL;
}

/* ------------------------------------------------------------------------- */
/* İçteki medya kaynağı                                                       */

static obs_data_t *child_settings(int port, int latency_ms)
{
	char url[160];
	obs_data_t *s = obs_data_create();

	/* FFmpeg'de SRT gecikmesi mikrosaniye cinsindendir. */
	snprintf(url, sizeof(url), "srt://0.0.0.0:%d?mode=listener&latency=%d", port, latency_ms * 1000);
	obs_data_set_bool(s, "is_local_file", false);
	obs_data_set_string(s, "input", url);
	obs_data_set_string(s, "input_format", "mpegts");
	obs_data_set_int(s, "reconnect_delay_sec", 1);
	obs_data_set_int(s, "buffering_mb", 1);
	obs_data_set_bool(s, "hw_decode", true);
	obs_data_set_bool(s, "restart_on_activate", false);
	obs_data_set_bool(s, "close_when_inactive", false);
	obs_data_set_bool(s, "clear_on_media_end", true);
	return s;
}

/* İçteki kaynağın sesi bu kaynağın sesi olarak yayınlanır; böylece ses mikserde "CamLink" adıyla görünür. */
static void forward_audio(void *param, obs_source_t *src, const struct audio_data *audio, bool muted)
{
	struct camlink *c = param;
	struct obs_audio_info oai;
	struct obs_source_audio out = {0};

	UNUSED_PARAMETER(src);
	if (muted || !obs_get_audio_info(&oai))
		return;

	for (size_t i = 0; i < MAX_AV_PLANES; i++)
		out.data[i] = audio->data[i];
	out.frames = audio->frames;
	out.timestamp = audio->timestamp;
	out.speakers = oai.speakers;
	out.format = AUDIO_FORMAT_FLOAT_PLANAR;
	out.samples_per_sec = oai.samples_per_sec;
	obs_source_output_audio(c->source, &out);
}

/* ------------------------------------------------------------------------- */
/* Kaynak                                                                    */

static const char *camlink_name(void *unused)
{
	UNUSED_PARAMETER(unused);
	return obs_module_text("CamLink.Name");
}

static void camlink_defaults(obs_data_t *settings)
{
	obs_data_set_default_int(settings, "port", 0);
	obs_data_set_default_int(settings, "latency", 120);
}

static void *camlink_create(obs_data_t *settings, obs_source_t *source)
{
	struct camlink *c = bzalloc(sizeof(struct camlink));
	char child_name[256];

	c->source = source;
	c->latency = (int)obs_data_get_int(settings, "latency");
	if (c->latency < 20)
		c->latency = 120;

	EnterCriticalSection(&cams_lock);
	c->port = pick_port((int)obs_data_get_int(settings, "port"), c);
	register_cam(c);
	LeaveCriticalSection(&cams_lock);

	/* Seçilen port ayarlara yazılır; OBS yeniden açılınca aynı port kullanılır. */
	obs_data_set_int(settings, "port", c->port);

	snprintf(child_name, sizeof(child_name), "camlink-%d", c->port);
	obs_data_t *cs = child_settings(c->port, c->latency);
	c->child = obs_source_create_private("ffmpeg_source", child_name, cs);
	obs_data_release(cs);

	if (c->child)
		obs_source_add_audio_capture_callback(c->child, forward_audio, c);
	else
		blog(LOG_WARNING, "[camlink] Medya kaynağı oluşturulamadı (obs-ffmpeg yüklü mü?)");

	blog(LOG_INFO, "[camlink] '%s' port %d üzerinde telefon bekliyor", obs_source_get_name(source), c->port);
	return c;
}

static void camlink_destroy(void *data)
{
	struct camlink *c = data;

	EnterCriticalSection(&cams_lock);
	unregister_cam(c);
	LeaveCriticalSection(&cams_lock);

	if (c->child) {
		obs_source_remove_audio_capture_callback(c->child, forward_audio, c);
		obs_source_release(c->child);
	}
	bfree(c);
}

static void camlink_update(void *data, obs_data_t *settings)
{
	struct camlink *c = data;
	int latency = (int)obs_data_get_int(settings, "latency");
	int port;

	if (latency < 20)
		latency = 120;

	EnterCriticalSection(&cams_lock);
	port = pick_port((int)obs_data_get_int(settings, "port"), c);
	LeaveCriticalSection(&cams_lock);

	if (port != (int)obs_data_get_int(settings, "port"))
		obs_data_set_int(settings, "port", port);

	if (port == c->port && latency == c->latency)
		return;

	c->port = port;
	c->latency = latency;
	if (c->child) {
		obs_data_t *cs = child_settings(port, latency);
		obs_source_update(c->child, cs);
		obs_data_release(cs);
	}
}

static uint32_t camlink_width(void *data)
{
	struct camlink *c = data;
	return c->child ? obs_source_get_width(c->child) : 0;
}

static uint32_t camlink_height(void *data)
{
	struct camlink *c = data;
	return c->child ? obs_source_get_height(c->child) : 0;
}

static void camlink_render(void *data, gs_effect_t *effect)
{
	struct camlink *c = data;
	UNUSED_PARAMETER(effect);
	if (c->child)
		obs_source_video_render(c->child);
}

static void camlink_enum_sources(void *data, obs_source_enum_proc_t cb, void *param)
{
	struct camlink *c = data;
	if (c->child)
		cb(c->source, c->child, param);
}

static bool restart_clicked(obs_properties_t *props, obs_property_t *p, void *data)
{
	struct camlink *c = data;
	UNUSED_PARAMETER(props);
	UNUSED_PARAMETER(p);
	if (c && c->child)
		obs_source_media_restart(c->child);
	return false;
}

#ifdef _WIN32
/* Bilgisayarın yerel ağ adresi; özellikler penceresinde elle bağlanma bilgisi olarak gösterilir. */
static bool primary_ipv4(char *out, size_t len)
{
	ULONG size = 16 * 1024;
	IP_ADAPTER_ADDRESSES *list = malloc(size);
	bool found = false;

	if (!list)
		return false;
	if (GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
				 NULL, list, &size) == ERROR_BUFFER_OVERFLOW) {
		free(list);
		list = malloc(size);
		if (!list)
			return false;
	}
	if (GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
				 NULL, list, &size) == NO_ERROR) {
		for (IP_ADAPTER_ADDRESSES *a = list; a && !found; a = a->Next) {
			if (a->OperStatus != IfOperStatusUp || a->IfType == IF_TYPE_SOFTWARE_LOOPBACK || !a->FirstGatewayAddress)
				continue;
			for (IP_ADAPTER_UNICAST_ADDRESS *u = a->FirstUnicastAddress; u; u = u->Next) {
				struct sockaddr_in *sin = (struct sockaddr_in *)u->Address.lpSockaddr;
				if (sin->sin_family != AF_INET)
					continue;
				inet_ntop(AF_INET, &sin->sin_addr, out, (socklen_t)len);
				found = true;
				break;
			}
		}
	}
	free(list);
	return found;
}
#else
/* Bilgisayarın yerel ağ adresi; özellikler penceresinde elle bağlanma bilgisi olarak gösterilir. */
static bool primary_ipv4(char *out, size_t len)
{
	struct ifaddrs *list, *a;
	bool found = false;

	if (getifaddrs(&list) != 0)
		return false;
	for (a = list; a && !found; a = a->ifa_next) {
		if (!a->ifa_addr || a->ifa_addr->sa_family != AF_INET || (a->ifa_flags & IFF_LOOPBACK) || !(a->ifa_flags & IFF_UP))
			continue;
		inet_ntop(AF_INET, &((struct sockaddr_in *)a->ifa_addr)->sin_addr, out, (socklen_t)len);
		found = true;
	}
	freeifaddrs(list);
	return found;
}
#endif

static obs_properties_t *camlink_properties(void *data)
{
	struct camlink *c = data;
	obs_properties_t *props = obs_properties_create();
	char info[512], ip[64] = "IP";

	obs_properties_add_int(props, "port", obs_module_text("CamLink.Port"), 1024, 65535, 1);
	obs_properties_add_int_slider(props, "latency", obs_module_text("CamLink.Latency"), 20, 1000, 10);

	primary_ipv4(ip, sizeof(ip));
	snprintf(info, sizeof(info), obs_module_text("CamLink.Info"), ip, c ? c->port : BASE_PORT);
	obs_property_t *p = obs_properties_add_text(props, "info", info, OBS_TEXT_INFO);
	obs_property_text_set_info_word_wrap(p, true);

	obs_properties_add_button2(props, "restart", obs_module_text("CamLink.Restart"), restart_clicked, c);
	return props;
}

static struct obs_source_info camlink_info = {
	.id = SOURCE_ID,
	.type = OBS_SOURCE_TYPE_INPUT,
	.output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_AUDIO | OBS_SOURCE_CUSTOM_DRAW | OBS_SOURCE_DO_NOT_DUPLICATE,
	.get_name = camlink_name,
	.create = camlink_create,
	.destroy = camlink_destroy,
	.update = camlink_update,
	.get_defaults = camlink_defaults,
	.get_properties = camlink_properties,
	.get_width = camlink_width,
	.get_height = camlink_height,
	.video_render = camlink_render,
	.enum_active_sources = camlink_enum_sources,
	.icon_type = OBS_ICON_TYPE_CAMERA,
};

/* ------------------------------------------------------------------------- */
/* Ağ duyurusu                                                                */

static void json_escape(const char *in, char *out, size_t len)
{
	size_t o = 0;
	for (const unsigned char *p = (const unsigned char *)in; *p && o + 7 < len; p++) {
		if (*p == '"' || *p == '\\') {
			out[o++] = '\\';
			out[o++] = (char)*p;
		} else if (*p < 0x20) {
			o += (size_t)snprintf(out + o, len - o, "\\u%04x", *p);
		} else {
			out[o++] = (char)*p;
		}
	}
	out[o] = 0;
}

static size_t build_payload(char *buf, size_t len, const char *host_name)
{
	char esc[256];
	size_t n;

	json_escape(host_name, esc, sizeof(esc));
	n = (size_t)snprintf(buf, len, "{\"app\":\"camlink-obs\",\"v\":1,\"name\":\"%s\",\"cams\":[", esc);

	EnterCriticalSection(&cams_lock);
	bool first = true;
	for (int i = 0; i < MAX_CAMS && n < len - 300; i++) {
		struct camlink *c = cams[i];
		if (!c)
			continue;
		json_escape(obs_source_get_name(c->source), esc, sizeof(esc));
		bool live = c->child && obs_source_media_get_state(c->child) == OBS_MEDIA_STATE_PLAYING;
		n += (size_t)snprintf(buf + n, len - n, "%s{\"name\":\"%s\",\"port\":%d,\"live\":%s}", first ? "" : ",",
				      esc, c->port, live ? "true" : "false");
		first = false;
	}
	LeaveCriticalSection(&cams_lock);

	n += (size_t)snprintf(buf + n, len - n, "]}");
	return n;
}

#ifdef _WIN32
/* Her bağdaştırıcının yönlendirilmiş yayın adresine ve 255.255.255.255'e gönderir. */
static void send_all(SOCKET s, const char *payload, int len)
{
	struct sockaddr_in to = {0};
	ULONG size = 16 * 1024;
	IP_ADAPTER_ADDRESSES *list = malloc(size);

	to.sin_family = AF_INET;
	to.sin_port = htons(BEACON_PORT);
	to.sin_addr.s_addr = INADDR_BROADCAST;
	sendto(s, payload, len, 0, (struct sockaddr *)&to, sizeof(to));

	if (!list)
		return;
	if (GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
				 NULL, list, &size) == ERROR_BUFFER_OVERFLOW) {
		free(list);
		list = malloc(size);
		if (!list)
			return;
	}
	if (GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
				 NULL, list, &size) == NO_ERROR) {
		for (IP_ADAPTER_ADDRESSES *a = list; a; a = a->Next) {
			if (a->OperStatus != IfOperStatusUp || a->IfType == IF_TYPE_SOFTWARE_LOOPBACK)
				continue;
			for (IP_ADAPTER_UNICAST_ADDRESS *u = a->FirstUnicastAddress; u; u = u->Next) {
				struct sockaddr_in *sin = (struct sockaddr_in *)u->Address.lpSockaddr;
				ULONG prefix = u->OnLinkPrefixLength;
				if (sin->sin_family != AF_INET || prefix == 0 || prefix >= 32)
					continue;
				ULONG mask = htonl(0xFFFFFFFFu << (32 - prefix));
				to.sin_addr.s_addr = sin->sin_addr.s_addr | ~mask;
				sendto(s, payload, len, 0, (struct sockaddr *)&to, sizeof(to));
			}
		}
	}
	free(list);
}

static HANDLE beacon_thread;
static HANDLE beacon_stop;

static DWORD WINAPI beacon_main(LPVOID unused)
{
	WSADATA wsa;
	char host[128] = "OBS", payload[4096];
	wchar_t whost[128];
	DWORD wlen = 128;
	BOOL yes = TRUE;

	UNUSED_PARAMETER(unused);
	if (GetComputerNameExW(ComputerNamePhysicalDnsHostname, whost, &wlen))
		WideCharToMultiByte(CP_UTF8, 0, whost, -1, host, sizeof(host), NULL, NULL);

	if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
		return 0;
	SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
	if (s == INVALID_SOCKET) {
		WSACleanup();
		return 0;
	}
	setsockopt(s, SOL_SOCKET, SO_BROADCAST, (const char *)&yes, sizeof(yes));

	do {
		size_t n = build_payload(payload, sizeof(payload), host);
		send_all(s, payload, (int)n);
	} while (WaitForSingleObject(beacon_stop, BEACON_INTERVAL_MS) == WAIT_TIMEOUT);

	closesocket(s);
	WSACleanup();
	return 0;
}
#else
/* Her arayüzün yayın adresine ve 255.255.255.255'e gönderir. */
static void send_all(int s, const char *payload, int len)
{
	struct sockaddr_in to = {0};
	struct ifaddrs *list, *a;

	to.sin_family = AF_INET;
	to.sin_port = htons(BEACON_PORT);
	to.sin_addr.s_addr = INADDR_BROADCAST;
	sendto(s, payload, (size_t)len, 0, (struct sockaddr *)&to, sizeof(to));

	if (getifaddrs(&list) != 0)
		return;
	for (a = list; a; a = a->ifa_next) {
		if (!a->ifa_addr || a->ifa_addr->sa_family != AF_INET || !(a->ifa_flags & IFF_BROADCAST) || !a->ifa_broadaddr)
			continue;
		to.sin_addr = ((struct sockaddr_in *)a->ifa_broadaddr)->sin_addr;
		sendto(s, payload, (size_t)len, 0, (struct sockaddr *)&to, sizeof(to));
	}
	freeifaddrs(list);
}

static pthread_t beacon_thread;
static volatile bool beacon_running;

static void *beacon_main(void *unused)
{
	char host[128] = "OBS", payload[4096];
	int yes = 1;

	UNUSED_PARAMETER(unused);
	gethostname(host, sizeof(host) - 1);
	int s = socket(AF_INET, SOCK_DGRAM, 0);
	if (s < 0)
		return NULL;
	setsockopt(s, SOL_SOCKET, SO_BROADCAST, &yes, sizeof(yes));
	while (beacon_running) {
		size_t n = build_payload(payload, sizeof(payload), host);
		send_all(s, payload, (int)n);
		for (int i = 0; i < BEACON_INTERVAL_MS / 100 && beacon_running; i++) {
			struct timespec ts = {0, 100 * 1000 * 1000};
			nanosleep(&ts, NULL);
		}
	}
	close(s);
	return NULL;
}
#endif

/* ------------------------------------------------------------------------- */

bool obs_module_load(void)
{
	InitializeCriticalSection(&cams_lock);
	obs_register_source(&camlink_info);

#ifdef _WIN32
	beacon_stop = CreateEventW(NULL, TRUE, FALSE, NULL);
	beacon_thread = CreateThread(NULL, 0, beacon_main, NULL, 0, NULL);
#else
	beacon_running = true;
	pthread_create(&beacon_thread, NULL, beacon_main, NULL);
#endif
	blog(LOG_INFO, "[camlink] Yüklendi; telefonlar UDP %d üzerinden duyuruyu görecek", BEACON_PORT);
	return true;
}

void obs_module_unload(void)
{
#ifdef _WIN32
	if (beacon_thread) {
		SetEvent(beacon_stop);
		WaitForSingleObject(beacon_thread, 5000);
		CloseHandle(beacon_thread);
		CloseHandle(beacon_stop);
		beacon_thread = NULL;
	}
#else
	if (beacon_running) {
		beacon_running = false;
		pthread_join(beacon_thread, NULL);
	}
#endif
	DeleteCriticalSection(&cams_lock);
}
