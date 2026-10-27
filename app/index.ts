import { registerRootComponent } from 'expo';
import { AppRegistry } from 'react-native';

import App from './App';

// registerRootComponent calls AppRegistry.registerComponent('main', () => App);
// It also ensures that whether you load the app in Expo Go or in a native build,
// the environment is set up appropriately
registerRootComponent(App);

// Yayın sürerken ekran kapanınca ya da uygulama arkaya geçince React Native JS zamanlayıcılarını durdurur; etkin bir
// Headless JS görevi varken sürdürür. CamlinkService (yerel ön plan hizmeti) bu görevi başlatır ve kapanırken bitirir;
// görev kendiliğinden hiç bitmez. Masaüstüyle kontrol kanalının kalp atışı ve yeniden bağlanma buna bağlı.
AppRegistry.registerHeadlessTask('CamLinkKeepAlive', () => () => new Promise<void>(() => {}));
