# プライバシーポリシー (Privacy Policy)

**最終更新日**: 2026年9月8日  
**対象ソフトウェア**: SanmiToys（以下「本アプリケーション」）

SanmiToys の開発チーム（以下「当方」）は、ユーザーのプライバシーを尊重し、個人情報の保護に最大限配慮しています。本プライバシーポリシーでは、本アプリケーションにおけるユーザー情報の取り扱い方針について説明します。

---

### 1. 収集する情報とその利用目的

本アプリケーションは、ユーザーの利便性向上（デスクトップ上でのカレンダー・バッテリー・システム情報等の常駐表示）を目的として、以下の情報にアクセスおよび利用します。

1. **Google カレンダー情報**:
   - **アクセスするデータ**: イベントのタイトル、日時、場所、説明、および Google アカウントのメールアドレス。
   - **利用目的**: Dynamic Island（OmniGlance）上での予定の表示、タイムライン通知、およびユーザー自身の操作による予定の追加・編集・削除のみに使用します。
2. **システムおよびハードウェア情報**:
   - **アクセスするデータ**: CPU/GPU/RAM の使用率、消費電力、接続されている Bluetooth デバイスのバッテリー残量。
   - **利用目的**: デスクトップ上でのリアルタイムシステムステータス表示のみに使用します。

---

### 2. データの保存場所とセキュリティ

1. **完全ローカル処理**:
   - 本アプリケーションが取得したすべてのカレンダー情報、システム情報、および認証トークンは、**ユーザーのローカル PC 内にのみ保存** されます。
   - 当方が管理する外部サーバー、データベース、またはサードパーティのサーバーにユーザーのデータが送信・収集・蓄積されることは一切ありません。
2. **認証情報のセキュアな暗号化**:
   - OAuth 2.0 リフレッシュトークンや App 用パスワードなどの機微な認証情報は、Windows 標準の暗号化基盤である **Windows DPAPI (Data Protection API)** により、ユーザー固有の暗号化キーを用いて安全にローカルに暗号化保存されます。

---

### 3. 第三者への開示・提供

本アプリケーションは、取得した個人情報やカレンダーデータをいかなる第三者にも開示、販売、貸与、または共有することはありません。

---

### 4. Google API ユーザーデータポリシーに関する声明 (Limited Use Disclosure)

本アプリケーションによる Google API から受け取った情報の使用および他のアプリへの転送は、[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy)（限定的使用要件を含む）に準拠します。

> **English Disclosure for Google OAuth Verification**:  
> SanmiToys' use and transfer to any other app of information received from Google APIs will adhere to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements. All Google Calendar data and user credentials accessed by SanmiToys are processed and stored exclusively on the user's local device, and are never transmitted to any third-party or developer-controlled servers.

---

### 5. ユーザーの権利とデータ削除

ユーザーはいつでも本アプリケーションの設定画面からログアウトを行うことができ、ログアウト時にはローカルに保存された認証トークンが直ちに完全削除されます。また、本アプリケーションをアンインストールすることで、すべてのローカル設定およびキャッシュデータを削除できます。

---

### 6. お問い合わせ

本プライバシーポリシーに関するご質問やお問い合わせは、本プロジェクトのリポジトリ（Issue / Pull Request 等）よりご連絡ください。
