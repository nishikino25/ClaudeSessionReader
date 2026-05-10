此檔案說明 Visual Studio 建立專案的方式。

下列工具用來產生此專案:
- Angular CLI (ng)

下列步驟用來產生此專案:
- 使用 ng 建立 Angular 專案: `ng new claudesessionreader.client --defaults --skip-install --skip-git --no-standalone `。
- 新增 `proxy.conf.js` 以將呼叫 Proxy 處理至後端 ASP.NET 伺服器。
- 新增 `aspnetcore-https.js` 指令碼以安裝 https 憑證。
- 更新 `package.json` 以呼叫 `aspnetcore-https.js` 並透過 https 提供服務。
- 更新 `angular.json` 以指向 `proxy.conf.js`。
- 更新 app.component.ts 元件以擷取和顯示天氣資訊。
- 使用已更新的測試修改 app.component.spec.ts。
- 更新 app.module.ts 以匯入 HttpClientModule。
- 建立專案檔 (`claudesessionreader.client.esproj`)。
- 建立 `launch.json` 以啟用偵錯功能。
- 更新 package.json 以新增 `jest-editor-support`。
- 更新 package.json 以新增 `run-script-os`。
- 新增 `karma.conf.js` 以進行單位測試。
- 更新 `angular.json` 以指向 `karma.conf.js`。
- 將專案新增至解決方案。
- 將 Proxy 端點更新為後端伺服器端點。
- 將專案新增至啟動專案清單。
- 寫入此檔案。
