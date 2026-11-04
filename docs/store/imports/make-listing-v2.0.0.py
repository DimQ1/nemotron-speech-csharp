"""Partner Center listing import for VoiceType 2.0.0.0 (the Uno app replacing VoiceType.WinUI, Store ID 9NRQX69XCLPT).

python docs/store/imports/make-listing-v2.0.0.py <fresh Partner Center export>  ->  v2.0.0/listingData-store-import-v2.0.0.0-multilang.csv.
Partner Center > Import listings > Import folder: choose v2.0.0/ (only the CSV and screenshots/ in it). Screenshot cells
must START WITH THE UPLOADED FOLDER'S NAME (v2.0.0/screenshots/...), see learn.microsoft.com .../msix/import-and-export-store-listings. Same columns as the Partner Center export, UTF-8 with BOM, CRLF.
Limits: ShortDescription 1000, Description 10000, ReleaseNotes 1500, captions and features 200 characters; checked below.
"""
import csv
import pathlib

HERE = pathlib.Path(__file__).parent / "v2.0.0"
LANGS = ["en-us", "ru-ru", "de-de", "fr-fr", "es-es", "zh-cn", "ja-jp", "ko-kr", "pt-br", "it-it", "ar-sa", "hi-in", "tr-tr", "uk-ua", "pl-pl", "nl-nl"]
TEXT = "Текст"
PATH = "Относительный путь (или URL-адрес файла в Центре партнеров)"
SCREENSHOTS = [
    "screenshots/01-history-speakers.png",
    "screenshots/02-live-translation.png",
    "screenshots/03-ask-gemma.png",
    "screenshots/04-translate-recording.png",
    "screenshots/05-typing-into-apps.png",
    "screenshots/06-hotkeys.png",
    "screenshots/07-models.png",
]
MAX_FEATURES = 20
OUT_NAME = "listingData-store-import-v2.0.0.0-multilang.csv"

# Per language: short, description, notes, captions (one per screenshot), features, search terms (7).
L = {}

L["en-us"] = dict(
    short="Offline voice typing for Windows: speak, and the text appears in any app. Every recording is kept with speakers, search and on-device AI translation and answers. Your audio never leaves your PC.",
    description="""VoiceType turns your voice into text on your PC. Press a hotkey, speak, and every finished phrase is typed into the app you are working in: email, documents, chat, a code editor or the browser. Speech recognition and AI run entirely on your device: no account, no cloud, no subscription.

DICTATE INTO ANY APP
• Global hotkeys start, stop and mute from any window (Ctrl+Shift+V by default)
• Text is typed as key presses, so your clipboard stays untouched; a paste mode is there for apps that need it
• Microphone, system audio or both: transcribe calls, videos and meetings, and switch the source while recording

A HISTORY OF EVERY RECORDING
• Audio, transcript and timestamps are kept together; play back from any sentence
• Who spoke when: speakers are detected on your PC and can be renamed
• Search across transcripts, translations and chats
• Recognize again with another model, edit the text, keep versions, export to TXT, SRT, LRC or Markdown

ON-DEVICE AI WITH GEMMA
• Live translation into 16 languages while you speak or type
• Translate finished recordings and read them next to the original
• Ask questions across your recordings and get answers that link to the exact moment

CHOOSE YOUR MODEL
• Parakeet TDT v3 (recommended), Nemotron 3.5 ASR and Qwen3-ASR, in full and compact sizes
• More than 30 languages, depending on the model, with automatic language detection
• Runs on any modern CPU; optional GPU acceleration on discrete graphics cards (experimental)

PRIVATE BY DESIGN
Audio, text and AI processing stay on your PC. The internet is used only to download the models you choose (from Hugging Face).

UPDATING FROM VERSION 1
Your settings and downloaded models are carried over at the first start, so there is nothing to download again.

Requirements: Windows 10 version 2004 or later, x64 processor, 8 GB RAM (16 GB recommended for the Gemma features), 0.5 to 2.5 GB of disk space for a speech model and 2.6 GB for Gemma.""",
    notes="VoiceType 2.0 is a new app, rebuilt from the ground up. New: History keeps every recording with its audio, transcript, timestamps and versions; speakers are detected on your PC; search across transcripts, translations and chats; Ask Gemma answers questions across recordings with links to the exact moment; translate whole recordings next to the original; new Settings with folders for models, recordings and app data; switch between microphone, system audio or both while recording; compact Parakeet and Nemotron models with smaller downloads. Your settings and downloaded models move over automatically at the first start: nothing has to be downloaded again. If your translation model was on another drive than your speech models, Settings > Models shows where it is. Custom text replacement rules of version 1 are not available in 2.0.",
    captions=[
        "Every recording with its transcript, timestamps and speakers, detected on your PC",
        "Type or dictate, and Gemma translates as you go, on the device",
        "Ask questions across recordings: answers link to the exact moment",
        "Translate a recording and read it next to the original",
        "Text goes straight into the app you are working in",
        "Global hotkeys work in any app",
        "Choose the speech model: accuracy, speed and download size at a glance",
    ],
    features=[
        "Dictation typed into any Windows app",
        "Global hotkeys to start, stop and mute",
        "Microphone, system audio or both, switchable while recording",
        "History with audio, transcript, timestamps and versions",
        "Speaker detection on your PC",
        "Search across transcripts, translations and chats",
        "Live translation into 16 languages with Gemma",
        "Ask questions across recordings, with cited answers",
        "Export to TXT, SRT, LRC and Markdown",
        "Parakeet, Nemotron and Qwen3-ASR models, 30+ languages",
        "Works offline: no account, no cloud",
        "Settings and models carried over from version 1",
    ],
    terms=["speech to text", "voice typing", "dictation", "offline transcription", "meeting transcription", "speaker diarization", "AI translation"],
)

L["ru-ru"] = dict(
    short="Голосовой ввод без интернета для Windows: говорите, и текст появляется в любом приложении. Каждая запись сохраняется с дикторами, поиском, переводом и ответами ИИ прямо на устройстве. Аудио не покидает ваш компьютер.",
    description="""VoiceType превращает голос в текст прямо на вашем компьютере. Нажмите горячую клавишу, говорите — и каждая законченная фраза печатается в приложение, в котором вы работаете: почту, документ, чат, редактор кода или браузер. Распознавание речи и ИИ работают полностью на устройстве: без аккаунта, без облака, без подписки.

ДИКТОВКА В ЛЮБОЕ ПРИЛОЖЕНИЕ
• Глобальные горячие клавиши: старт, стоп и отключение микрофона из любого окна (по умолчанию Ctrl+Shift+V)
• Текст вводится нажатиями клавиш, буфер обмена не трогается; для особых приложений есть режим вставки
• Микрофон, звук системы или оба сразу: расшифровка звонков, видео и встреч, источник можно сменить во время записи

ИСТОРИЯ ВСЕХ ЗАПИСЕЙ
• Аудио, текст и метки времени хранятся вместе; воспроизведение с любой фразы
• Кто и когда говорил: дикторы определяются на вашем компьютере, их можно переименовать
• Поиск по расшифровкам, переводам и чатам
• Повторное распознавание другой моделью, правка текста, версии, экспорт в TXT, SRT, LRC и Markdown

ИИ НА УСТРОЙСТВЕ: GEMMA
• Живой перевод на 16 языков, пока вы говорите или печатаете
• Перевод готовых записей рядом с оригиналом
• Вопросы по нескольким записям: ответы со ссылками на нужный момент

ВЫБОР МОДЕЛИ
• Parakeet TDT v3 (рекомендуется), Nemotron 3.5 ASR и Qwen3-ASR, в полных и компактных версиях
• Более 30 языков в зависимости от модели, автоопределение языка
• Работает на любом современном процессоре; ускорение на дискретной видеокарте (экспериментально)

КОНФИДЕНЦИАЛЬНОСТЬ
Аудио, текст и работа ИИ остаются на вашем компьютере. Интернет нужен только чтобы скачать выбранные модели (с Hugging Face).

ОБНОВЛЕНИЕ С ВЕРСИИ 1
Настройки и скачанные модели переносятся при первом запуске, ничего не нужно скачивать заново.

Требования: Windows 10 версии 2004 или новее, процессор x64, 8 ГБ ОЗУ (16 ГБ для функций Gemma), от 0,5 до 2,5 ГБ на диске для модели распознавания и 2,6 ГБ для Gemma.""",
    notes="VoiceType 2.0 — новое приложение, переписанное с нуля. Новое: История хранит каждую запись с аудио, текстом, метками времени и версиями; дикторы определяются на вашем компьютере; поиск по расшифровкам, переводам и чатам; «Спросить Gemma» отвечает на вопросы по нескольким записям со ссылками на нужный момент; перевод целых записей рядом с оригиналом; новые Настройки с папками для моделей, записей и данных приложения; переключение между микрофоном, звуком системы или обоими во время записи; компактные модели Parakeet и Nemotron с меньшим размером загрузки. Настройки и скачанные модели переносятся автоматически при первом запуске — заново ничего скачивать не нужно. Если модель перевода лежала на другом диске, чем модели распознавания, раздел «Настройки > Модели» покажет, где она. Пользовательские правила замены текста из версии 1 в 2.0 недоступны.",
    captions=[
        "Каждая запись с текстом, метками времени и дикторами, определёнными на вашем компьютере",
        "Печатайте или диктуйте — Gemma переводит на ходу, прямо на устройстве",
        "Вопросы по нескольким записям: ответы ведут к нужному моменту",
        "Перевод записи рядом с оригиналом",
        "Текст сразу попадает в приложение, в котором вы работаете",
        "Глобальные горячие клавиши работают в любом приложении",
        "Выбор модели распознавания: точность, скорость и размер загрузки",
    ],
    features=[
        "Диктовка с вводом текста в любое приложение Windows",
        "Глобальные горячие клавиши: старт, стоп, отключение микрофона",
        "Микрофон, звук системы или оба, переключение во время записи",
        "История с аудио, текстом, метками времени и версиями",
        "Определение дикторов на вашем компьютере",
        "Поиск по расшифровкам, переводам и чатам",
        "Живой перевод на 16 языков с Gemma",
        "Вопросы по записям с ответами со ссылками",
        "Экспорт в TXT, SRT, LRC и Markdown",
        "Модели Parakeet, Nemotron и Qwen3-ASR, более 30 языков",
        "Работает без интернета: без аккаунта и облака",
        "Настройки и модели переносятся из версии 1",
    ],
    terms=["голосовой ввод", "распознавание речи", "диктовка", "расшифровка аудио", "речь в текст", "протокол встречи", "перевод ИИ"],
)

L["de-de"] = dict(
    short="Offline-Spracheingabe für Windows: Sprechen Sie, und der Text erscheint in jeder App. Jede Aufnahme bleibt mit Sprechern, Suche sowie KI-Übersetzung und -Antworten auf dem Gerät erhalten. Ihr Audio verlässt den PC nie.",
    description="""VoiceType verwandelt Ihre Stimme direkt auf dem PC in Text. Drücken Sie ein Tastenkürzel, sprechen Sie, und jeder fertige Satz wird in die App getippt, in der Sie gerade arbeiten: E-Mail, Dokument, Chat, Code-Editor oder Browser. Spracherkennung und KI laufen vollständig auf Ihrem Gerät: kein Konto, keine Cloud, kein Abo.

DIKTIEREN IN JEDE APP
• Globale Tastenkürzel zum Starten, Stoppen und Stummschalten aus jedem Fenster (Standard: Strg+Umschalt+V)
• Der Text wird als Tastendruck eingegeben, die Zwischenablage bleibt unberührt; für besondere Apps gibt es einen Einfügemodus
• Mikrofon, Systemton oder beides: Anrufe, Videos und Besprechungen transkribieren, die Quelle während der Aufnahme wechseln

VERLAUF ALLER AUFNAHMEN
• Audio, Transkript und Zeitmarken bleiben zusammen; Wiedergabe ab jedem Satz
• Wer hat wann gesprochen: Sprecher werden auf Ihrem PC erkannt und lassen sich umbenennen
• Suche in Transkripten, Übersetzungen und Chats
• Mit einem anderen Modell neu erkennen, Text bearbeiten, Versionen behalten, Export als TXT, SRT, LRC oder Markdown

KI AUF DEM GERÄT MIT GEMMA
• Live-Übersetzung in 16 Sprachen, während Sie sprechen oder tippen
• Fertige Aufnahmen übersetzen und neben dem Original lesen
• Fragen zu mehreren Aufnahmen stellen und Antworten mit Links zur genauen Stelle erhalten

IHR MODELL
• Parakeet TDT v3 (empfohlen), Nemotron 3.5 ASR und Qwen3-ASR, in voller und kompakter Größe
• Mehr als 30 Sprachen je nach Modell, mit automatischer Spracherkennung
• Läuft auf jeder modernen CPU; optionale GPU-Beschleunigung auf dedizierten Grafikkarten (experimentell)

DATENSCHUTZ
Audio, Text und KI-Verarbeitung bleiben auf Ihrem PC. Das Internet wird nur zum Herunterladen der gewählten Modelle (von Hugging Face) verwendet.

UPDATE VON VERSION 1
Ihre Einstellungen und heruntergeladenen Modelle werden beim ersten Start übernommen, nichts muss erneut heruntergeladen werden.

Voraussetzungen: Windows 10 Version 2004 oder neuer, x64-Prozessor, 8 GB RAM (16 GB empfohlen für die Gemma-Funktionen), 0,5 bis 2,5 GB Speicher für ein Sprachmodell und 2,6 GB für Gemma.""",
    notes="VoiceType 2.0 ist eine neue App, von Grund auf neu entwickelt. Neu: Der Verlauf speichert jede Aufnahme mit Audio, Transkript, Zeitmarken und Versionen; Sprecher werden auf Ihrem PC erkannt; Suche in Transkripten, Übersetzungen und Chats; „Gemma fragen“ beantwortet Fragen zu mehreren Aufnahmen mit Links zur genauen Stelle; ganze Aufnahmen neben dem Original übersetzen; neue Einstellungen mit Ordnern für Modelle, Aufnahmen und App-Daten; während der Aufnahme zwischen Mikrofon, Systemton oder beidem wechseln; kompakte Parakeet- und Nemotron-Modelle mit kleineren Downloads. Einstellungen und heruntergeladene Modelle werden beim ersten Start automatisch übernommen: Nichts muss erneut heruntergeladen werden. Lag Ihr Übersetzungsmodell auf einem anderen Laufwerk als die Sprachmodelle, zeigt Einstellungen > Modelle, wo es liegt. Eigene Textersetzungsregeln aus Version 1 sind in 2.0 nicht verfügbar.",
    captions=[
        "Jede Aufnahme mit Transkript, Zeitmarken und Sprechern, erkannt auf Ihrem PC",
        "Tippen oder diktieren – Gemma übersetzt sofort, auf dem Gerät",
        "Fragen zu mehreren Aufnahmen: Antworten führen zur genauen Stelle",
        "Eine Aufnahme übersetzen und neben dem Original lesen",
        "Der Text landet direkt in der App, in der Sie arbeiten",
        "Globale Tastenkürzel funktionieren in jeder App",
        "Sprachmodell wählen: Genauigkeit, Tempo und Downloadgröße auf einen Blick",
    ],
    features=[
        "Diktieren direkt in jede Windows-App",
        "Globale Tastenkürzel zum Starten, Stoppen und Stummschalten",
        "Mikrofon, Systemton oder beides, während der Aufnahme umschaltbar",
        "Verlauf mit Audio, Transkript, Zeitmarken und Versionen",
        "Sprechererkennung auf Ihrem PC",
        "Suche in Transkripten, Übersetzungen und Chats",
        "Live-Übersetzung in 16 Sprachen mit Gemma",
        "Fragen zu Aufnahmen mit belegten Antworten",
        "Export als TXT, SRT, LRC und Markdown",
        "Modelle Parakeet, Nemotron und Qwen3-ASR, über 30 Sprachen",
        "Funktioniert offline: kein Konto, keine Cloud",
        "Einstellungen und Modelle aus Version 1 werden übernommen",
    ],
    terms=["Spracheingabe", "Sprache zu Text", "Diktieren", "Transkription offline", "Besprechungsprotokoll", "Sprechererkennung", "KI Übersetzung"],
)

L["fr-fr"] = dict(
    short="Saisie vocale hors ligne pour Windows : parlez, le texte apparaît dans n'importe quelle application. Chaque enregistrement est conservé avec les intervenants, la recherche, la traduction et les réponses de l'IA sur l'appareil. Votre audio ne quitte jamais votre PC.",
    description="""VoiceType transforme votre voix en texte directement sur votre PC. Appuyez sur un raccourci, parlez, et chaque phrase terminée est saisie dans l'application où vous travaillez : e-mail, document, messagerie, éditeur de code ou navigateur. La reconnaissance vocale et l'IA fonctionnent entièrement sur votre appareil : sans compte, sans cloud, sans abonnement.

DICTEZ DANS N'IMPORTE QUELLE APPLICATION
• Raccourcis globaux pour démarrer, arrêter et couper le micro depuis n'importe quelle fenêtre (Ctrl+Maj+V par défaut)
• Le texte est saisi comme des frappes au clavier, le presse-papiers reste intact ; un mode collage existe pour les applications qui en ont besoin
• Micro, son du système ou les deux : transcrivez appels, vidéos et réunions, et changez de source pendant l'enregistrement

L'HISTORIQUE DE CHAQUE ENREGISTREMENT
• Audio, transcription et horodatage restent ensemble ; lecture à partir de n'importe quelle phrase
• Qui a parlé quand : les intervenants sont détectés sur votre PC et peuvent être renommés
• Recherche dans les transcriptions, les traductions et les conversations
• Nouvelle reconnaissance avec un autre modèle, modification du texte, versions, export en TXT, SRT, LRC ou Markdown

IA SUR L'APPAREIL AVEC GEMMA
• Traduction en direct dans 16 langues pendant que vous parlez ou tapez
• Traduisez des enregistrements et lisez-les à côté de l'original
• Posez des questions sur plusieurs enregistrements et obtenez des réponses qui renvoient au moment exact

CHOISISSEZ VOTRE MODÈLE
• Parakeet TDT v3 (recommandé), Nemotron 3.5 ASR et Qwen3-ASR, en versions complètes et compactes
• Plus de 30 langues selon le modèle, avec détection automatique de la langue
• Fonctionne sur tout processeur récent ; accélération GPU facultative sur carte graphique dédiée (expérimental)

CONFIDENTIALITÉ
L'audio, le texte et le traitement par l'IA restent sur votre PC. Internet ne sert qu'à télécharger les modèles choisis (depuis Hugging Face).

MISE À JOUR DEPUIS LA VERSION 1
Vos paramètres et vos modèles téléchargés sont repris au premier démarrage, rien n'est à retélécharger.

Configuration requise : Windows 10 version 2004 ou ultérieure, processeur x64, 8 Go de RAM (16 Go recommandés pour les fonctions Gemma), 0,5 à 2,5 Go d'espace disque pour un modèle vocal et 2,6 Go pour Gemma.""",
    notes="VoiceType 2.0 est une nouvelle application, entièrement repensée. Nouveau : l'Historique conserve chaque enregistrement avec son audio, sa transcription, son horodatage et ses versions ; les intervenants sont détectés sur votre PC ; recherche dans les transcriptions, traductions et conversations ; « Demander à Gemma » répond aux questions sur plusieurs enregistrements avec des liens vers le moment exact ; traduction d'enregistrements entiers à côté de l'original ; nouveaux Paramètres avec des dossiers pour les modèles, les enregistrements et les données de l'application ; passage du micro au son du système ou aux deux pendant l'enregistrement ; modèles Parakeet et Nemotron compacts, plus légers à télécharger. Vos paramètres et modèles téléchargés sont repris automatiquement au premier démarrage : rien n'est à retélécharger. Si votre modèle de traduction se trouvait sur un autre disque que vos modèles vocaux, Paramètres > Modèles indique où il se trouve. Les règles personnalisées de remplacement de texte de la version 1 ne sont pas disponibles dans la 2.0.",
    captions=[
        "Chaque enregistrement avec transcription, horodatage et intervenants détectés sur votre PC",
        "Tapez ou dictez, Gemma traduit au fil de l'eau, sur l'appareil",
        "Questions sur plusieurs enregistrements : les réponses renvoient au moment exact",
        "Traduisez un enregistrement et lisez-le à côté de l'original",
        "Le texte arrive directement dans l'application où vous travaillez",
        "Les raccourcis globaux fonctionnent dans toutes les applications",
        "Choisissez le modèle vocal : précision, vitesse et taille en un coup d'œil",
    ],
    features=[
        "Dictée saisie dans n'importe quelle application Windows",
        "Raccourcis globaux pour démarrer, arrêter et couper le micro",
        "Micro, son du système ou les deux, modifiable pendant l'enregistrement",
        "Historique avec audio, transcription, horodatage et versions",
        "Détection des intervenants sur votre PC",
        "Recherche dans les transcriptions, traductions et conversations",
        "Traduction en direct dans 16 langues avec Gemma",
        "Questions sur les enregistrements avec réponses sourcées",
        "Export en TXT, SRT, LRC et Markdown",
        "Modèles Parakeet, Nemotron et Qwen3-ASR, plus de 30 langues",
        "Fonctionne hors ligne : sans compte, sans cloud",
        "Paramètres et modèles repris de la version 1",
    ],
    terms=["saisie vocale", "reconnaissance vocale", "dictée", "transcription hors ligne", "compte rendu de réunion", "identification des locuteurs", "traduction IA"],
)

L["es-es"] = dict(
    short="Escritura por voz sin conexión para Windows: hable y el texto aparece en cualquier aplicación. Cada grabación se guarda con hablantes, búsqueda, traducción y respuestas de IA en el propio equipo. Su audio nunca sale de su PC.",
    description="""VoiceType convierte su voz en texto en su propio PC. Pulse un atajo, hable y cada frase terminada se escribe en la aplicación en la que trabaja: correo, documento, chat, editor de código o navegador. El reconocimiento de voz y la IA funcionan por completo en su dispositivo: sin cuenta, sin nube, sin suscripción.

DICTE EN CUALQUIER APLICACIÓN
• Atajos globales para iniciar, detener y silenciar desde cualquier ventana (Ctrl+Mayús+V por defecto)
• El texto se escribe como pulsaciones de teclas y el portapapeles no se toca; hay un modo de pegado para las aplicaciones que lo necesitan
• Micrófono, audio del sistema o ambos: transcriba llamadas, vídeos y reuniones, y cambie la fuente mientras graba

UN HISTORIAL DE CADA GRABACIÓN
• Audio, transcripción y marcas de tiempo se guardan juntos; reproduzca desde cualquier frase
• Quién habló y cuándo: los hablantes se detectan en su PC y se pueden renombrar
• Busque en transcripciones, traducciones y chats
• Vuelva a reconocer con otro modelo, edite el texto, conserve versiones y exporte a TXT, SRT, LRC o Markdown

IA EN EL DISPOSITIVO CON GEMMA
• Traducción en directo a 16 idiomas mientras habla o escribe
• Traduzca grabaciones terminadas y léalas junto al original
• Haga preguntas sobre varias grabaciones y reciba respuestas con enlaces al momento exacto

ELIJA SU MODELO
• Parakeet TDT v3 (recomendado), Nemotron 3.5 ASR y Qwen3-ASR, en versiones completas y compactas
• Más de 30 idiomas según el modelo, con detección automática del idioma
• Funciona en cualquier procesador moderno; aceleración por GPU opcional en tarjetas gráficas dedicadas (experimental)

PRIVACIDAD
El audio, el texto y el procesamiento de IA permanecen en su PC. Internet solo se usa para descargar los modelos que elija (desde Hugging Face).

ACTUALIZACIÓN DESDE LA VERSIÓN 1
Su configuración y los modelos descargados se conservan en el primer inicio; no hay que volver a descargar nada.

Requisitos: Windows 10 versión 2004 o posterior, procesador x64, 8 GB de RAM (16 GB recomendados para las funciones de Gemma), de 0,5 a 2,5 GB de disco para un modelo de voz y 2,6 GB para Gemma.""",
    notes="VoiceType 2.0 es una aplicación nueva, rehecha desde cero. Novedades: el Historial guarda cada grabación con su audio, transcripción, marcas de tiempo y versiones; los hablantes se detectan en su PC; búsqueda en transcripciones, traducciones y chats; «Preguntar a Gemma» responde preguntas sobre varias grabaciones con enlaces al momento exacto; traducción de grabaciones completas junto al original; nueva Configuración con carpetas para modelos, grabaciones y datos de la aplicación; cambio entre micrófono, audio del sistema o ambos durante la grabación; modelos Parakeet y Nemotron compactos con descargas más pequeñas. La configuración y los modelos descargados se trasladan automáticamente en el primer inicio: no hay que volver a descargar nada. Si su modelo de traducción estaba en otra unidad que los modelos de voz, Configuración > Modelos indica dónde está. Las reglas personalizadas de sustitución de texto de la versión 1 no están disponibles en la 2.0.",
    captions=[
        "Cada grabación con su transcripción, marcas de tiempo y hablantes detectados en su PC",
        "Escriba o dicte y Gemma traduce al momento, en el dispositivo",
        "Preguntas sobre varias grabaciones: las respuestas llevan al momento exacto",
        "Traduzca una grabación y léala junto al original",
        "El texto llega directamente a la aplicación en la que trabaja",
        "Los atajos globales funcionan en cualquier aplicación",
        "Elija el modelo de voz: precisión, velocidad y tamaño de un vistazo",
    ],
    features=[
        "Dictado que se escribe en cualquier aplicación de Windows",
        "Atajos globales para iniciar, detener y silenciar",
        "Micrófono, audio del sistema o ambos, cambiables durante la grabación",
        "Historial con audio, transcripción, marcas de tiempo y versiones",
        "Detección de hablantes en su PC",
        "Búsqueda en transcripciones, traducciones y chats",
        "Traducción en directo a 16 idiomas con Gemma",
        "Preguntas sobre grabaciones con respuestas citadas",
        "Exportación a TXT, SRT, LRC y Markdown",
        "Modelos Parakeet, Nemotron y Qwen3-ASR, más de 30 idiomas",
        "Funciona sin conexión: sin cuenta y sin nube",
        "Configuración y modelos conservados desde la versión 1",
    ],
    terms=["dictado por voz", "voz a texto", "dictado", "transcripción sin conexión", "transcribir reuniones", "identificación de hablantes", "traducción con IA"],
)

L["zh-cn"] = dict(
    short="Windows 离线语音输入：开口说话，文字即出现在任意应用中。每段录音都会保存，并在本机提供说话人识别、搜索、AI 翻译和问答。音频从不离开您的电脑。",
    description="""VoiceType 在您的电脑上把语音变成文字。按下快捷键开始说话，每说完一句，文字就会输入到您正在使用的应用中：邮件、文档、聊天、代码编辑器或浏览器。语音识别和 AI 完全在本机运行：无需账户，无需云端，无需订阅。

在任意应用中听写
• 全局快捷键，在任何窗口中开始、停止和静音（默认 Ctrl+Shift+V）
• 文字以按键方式输入，不占用剪贴板；需要时可改用粘贴模式
• 麦克风、系统声音或两者同时：转写通话、视频和会议，录音时也可切换音源

每段录音都有记录
• 音频、文字和时间戳保存在一起，可从任意一句开始播放
• 谁在何时发言：在本机识别说话人，并可重命名
• 在转写、翻译和对话中搜索
• 用其他模型重新识别、编辑文字、保留版本，导出为 TXT、SRT、LRC 或 Markdown

本机 AI：GEMMA
• 说话或打字时实时翻译成 16 种语言
• 翻译已完成的录音，与原文对照阅读
• 针对多段录音提问，回答附带指向具体时间点的链接

选择模型
• Parakeet TDT v3（推荐）、Nemotron 3.5 ASR 和 Qwen3-ASR，提供完整版和精简版
• 视模型支持 30 多种语言，可自动识别语言
• 可在任何现代 CPU 上运行；可选在独立显卡上进行 GPU 加速（实验性）

隐私
音频、文字和 AI 处理都留在您的电脑上。仅在下载您选择的模型（来自 Hugging Face）时使用网络。

从版本 1 升级
首次启动时会自动保留您的设置和已下载的模型，无需重新下载。

系统要求：Windows 10 版本 2004 或更高，x64 处理器，8 GB 内存（使用 Gemma 功能建议 16 GB），语音模型需 0.5–2.5 GB 磁盘空间，Gemma 需 2.6 GB。""",
    notes="VoiceType 2.0 是全新开发的应用。新功能：历史记录保存每段录音的音频、文字、时间戳和版本；在本机识别说话人；在转写、翻译和对话中搜索；“询问 Gemma”可针对多段录音回答问题，并链接到具体时间点；整段录音翻译并与原文对照；全新设置，可为模型、录音和应用数据选择文件夹；录音时可在麦克风、系统声音或两者之间切换；精简版 Parakeet 和 Nemotron 模型，下载更小。首次启动时会自动迁移您的设置和已下载的模型，无需重新下载。如果翻译模型与语音模型位于不同的磁盘，“设置 > 模型”会显示它的位置。版本 1 中的自定义文本替换规则在 2.0 中不可用。",
    captions=[
        "每段录音都有文字、时间戳和在本机识别的说话人",
        "打字或听写，Gemma 在本机即时翻译",
        "针对多段录音提问：回答直达具体时间点",
        "翻译录音并与原文对照阅读",
        "文字直接输入到您正在使用的应用",
        "全局快捷键在任何应用中都可用",
        "选择语音模型：准确率、速度和下载大小一目了然",
    ],
    features=[
        "听写内容直接输入任意 Windows 应用",
        "全局快捷键：开始、停止、静音",
        "麦克风、系统声音或两者，录音时可切换",
        "历史记录：音频、文字、时间戳和版本",
        "在本机识别说话人",
        "在转写、翻译和对话中搜索",
        "借助 Gemma 实时翻译成 16 种语言",
        "针对录音提问，回答附带出处",
        "导出为 TXT、SRT、LRC 和 Markdown",
        "Parakeet、Nemotron 和 Qwen3-ASR 模型，30 多种语言",
        "离线可用：无需账户，无需云端",
        "保留版本 1 的设置和模型",
    ],
    terms=["语音输入", "语音转文字", "听写", "离线转写", "会议记录", "说话人识别", "AI 翻译"],
)

L["ja-jp"] = dict(
    short="Windows 用のオフライン音声入力。話すだけで、どのアプリにも文字が入力されます。録音はすべて保存され、話者の識別、検索、AI による翻訳と回答をデバイス上で利用できます。音声が PC の外に出ることはありません。",
    description="""VoiceType は、あなたの声を PC 上でテキストに変換します。ショートカットキーを押して話すと、話し終えた文が、今使っているアプリ（メール、文書、チャット、コードエディター、ブラウザー）に入力されます。音声認識と AI はすべてデバイス上で動作します。アカウントもクラウドもサブスクリプションも不要です。

どのアプリにも音声入力
• グローバルショートカットで、どのウィンドウからでも開始・停止・ミュート（既定は Ctrl+Shift+V）
• キー入力として文字を送るため、クリップボードはそのまま。必要なアプリには貼り付けモードも用意
• マイク、システム音声、またはその両方。通話・動画・会議を文字起こしし、録音中に音源を切り替え可能

すべての録音を履歴に
• 音声・テキスト・タイムスタンプを一緒に保存し、どの文からでも再生
• 誰がいつ話したか：話者を PC 上で識別し、名前を変更可能
• 文字起こし、翻訳、チャットを横断して検索
• 別のモデルで再認識、テキストの編集、バージョン管理、TXT・SRT・LRC・Markdown へのエクスポート

デバイス上の AI：GEMMA
• 話しながら、または入力しながら 16 言語へライブ翻訳
• 録音を翻訳し、原文と並べて表示
• 複数の録音について質問すると、該当箇所へのリンク付きで回答

モデルを選べる
• Parakeet TDT v3（推奨）、Nemotron 3.5 ASR、Qwen3-ASR。フル版とコンパクト版
• モデルに応じて 30 以上の言語に対応、言語の自動判別
• 最新の CPU で動作。専用グラフィックスカードでの GPU 高速化も選択可能（試験的）

プライバシー
音声、テキスト、AI の処理はすべて PC 内にとどまります。インターネットは、選んだモデルを Hugging Face からダウンロードするときにだけ使います。

バージョン 1 からの更新
設定とダウンロード済みのモデルは初回起動時に引き継がれるため、再ダウンロードは不要です。

動作環境：Windows 10 バージョン 2004 以降、x64 プロセッサ、メモリ 8 GB（Gemma の機能には 16 GB を推奨）、音声モデル用に 0.5～2.5 GB、Gemma 用に 2.6 GB のディスク空き容量。""",
    notes="VoiceType 2.0 は一から作り直した新しいアプリです。新機能：履歴に、録音ごとの音声・テキスト・タイムスタンプ・バージョンを保存。PC 上で話者を識別。文字起こし・翻訳・チャットを横断検索。「Gemma に質問」で複数の録音について質問でき、回答は該当箇所にリンク。録音全体を原文と並べて翻訳。モデル、録音、アプリデータのフォルダーを選べる新しい設定画面。録音中にマイク、システム音声、両方を切り替え。ダウンロードサイズの小さいコンパクト版 Parakeet と Nemotron。設定とダウンロード済みモデルは初回起動時に自動で引き継がれ、再ダウンロードは不要です。翻訳モデルが音声モデルと別のドライブにあった場合は、［設定］>［モデル］にその場所が表示されます。バージョン 1 のテキスト置換ルールは 2.0 では使用できません。",
    captions=[
        "録音ごとにテキスト、タイムスタンプ、PC 上で識別した話者を表示",
        "入力しても話しても、Gemma がデバイス上ですぐに翻訳",
        "複数の録音に質問：回答から該当箇所へ移動",
        "録音を翻訳し、原文と並べて確認",
        "テキストは作業中のアプリに直接入力",
        "グローバルショートカットはどのアプリでも使用可能",
        "音声モデルを選択：精度、速度、ダウンロードサイズをひと目で比較",
    ],
    features=[
        "どの Windows アプリにも音声で入力",
        "グローバルショートカットで開始・停止・ミュート",
        "マイク、システム音声、両方を録音中に切り替え",
        "音声・テキスト・タイムスタンプ・バージョン付きの履歴",
        "PC 上での話者識別",
        "文字起こし・翻訳・チャットの横断検索",
        "Gemma による 16 言語へのライブ翻訳",
        "録音への質問に出典付きで回答",
        "TXT、SRT、LRC、Markdown へエクスポート",
        "Parakeet、Nemotron、Qwen3-ASR モデル、30 以上の言語",
        "オフラインで動作：アカウントもクラウドも不要",
        "バージョン 1 の設定とモデルを引き継ぎ",
    ],
    terms=["音声入力", "音声認識", "ディクテーション", "文字起こし", "議事録", "話者識別", "AI 翻訳"],
)

L["ko-kr"] = dict(
    short="Windows용 오프라인 음성 입력: 말하면 어떤 앱에든 텍스트가 입력됩니다. 모든 녹음은 화자 구분, 검색, AI 번역과 답변과 함께 기기에 저장됩니다. 오디오는 PC를 벗어나지 않습니다.",
    description="""VoiceType은 PC에서 바로 음성을 텍스트로 바꿉니다. 단축키를 누르고 말하면, 문장이 끝날 때마다 지금 사용 중인 앱(이메일, 문서, 채팅, 코드 편집기, 브라우저)에 입력됩니다. 음성 인식과 AI는 모두 기기에서 실행됩니다. 계정도, 클라우드도, 구독도 필요 없습니다.

어떤 앱에서든 받아쓰기
• 전역 단축키로 어느 창에서든 시작, 중지, 음소거(기본값 Ctrl+Shift+V)
• 텍스트를 키 입력으로 보내므로 클립보드는 그대로 유지되며, 필요한 앱을 위한 붙여넣기 모드도 있습니다
• 마이크, 시스템 오디오 또는 둘 다: 통화, 동영상, 회의를 받아쓰고 녹음 중에도 입력 소스를 전환

모든 녹음의 기록
• 오디오, 텍스트, 타임스탬프를 함께 저장하고 원하는 문장부터 재생
• 누가 언제 말했는지: PC에서 화자를 구분하고 이름을 바꿀 수 있습니다
• 받아쓰기, 번역, 채팅을 한 번에 검색
• 다른 모델로 다시 인식, 텍스트 편집, 버전 보관, TXT·SRT·LRC·Markdown으로 내보내기

기기 내 AI: GEMMA
• 말하거나 입력하는 동안 16개 언어로 실시간 번역
• 완료된 녹음을 번역해 원문과 나란히 보기
• 여러 녹음에 대해 질문하고, 해당 순간으로 연결되는 답변 받기

모델 선택
• Parakeet TDT v3(권장), Nemotron 3.5 ASR, Qwen3-ASR의 전체 및 소형 버전
• 모델에 따라 30개 이상의 언어 지원, 언어 자동 감지
• 최신 CPU에서 실행되며, 외장 그래픽 카드에서 GPU 가속 선택 가능(실험적)

개인정보 보호
오디오, 텍스트, AI 처리는 모두 PC에 남습니다. 인터넷은 선택한 모델을 Hugging Face에서 내려받을 때만 사용됩니다.

버전 1에서 업데이트
설정과 내려받은 모델은 처음 실행할 때 그대로 옮겨지므로 다시 내려받을 필요가 없습니다.

요구 사항: Windows 10 버전 2004 이상, x64 프로세서, RAM 8GB(Gemma 기능에는 16GB 권장), 음성 모델용 디스크 공간 0.5~2.5GB와 Gemma용 2.6GB.""",
    notes="VoiceType 2.0은 처음부터 새로 만든 앱입니다. 새로운 기능: 기록에 모든 녹음의 오디오, 텍스트, 타임스탬프, 버전을 저장; PC에서 화자 구분; 받아쓰기, 번역, 채팅 통합 검색; 'Gemma에게 묻기'로 여러 녹음에 대한 질문에 해당 순간 링크와 함께 답변; 녹음 전체를 원문과 나란히 번역; 모델, 녹음, 앱 데이터 폴더를 고를 수 있는 새 설정; 녹음 중 마이크, 시스템 오디오, 둘 다 사이 전환; 다운로드 크기가 작은 소형 Parakeet 및 Nemotron 모델. 설정과 내려받은 모델은 처음 실행할 때 자동으로 옮겨지므로 다시 내려받을 필요가 없습니다. 번역 모델이 음성 모델과 다른 드라이브에 있었다면 설정 > 모델에서 위치를 알려 줍니다. 버전 1의 사용자 지정 텍스트 바꾸기 규칙은 2.0에서 사용할 수 없습니다.",
    captions=[
        "모든 녹음에 텍스트, 타임스탬프, PC에서 구분한 화자 표시",
        "입력하거나 말하면 Gemma가 기기에서 바로 번역",
        "여러 녹음에 질문: 답변이 해당 순간으로 연결",
        "녹음을 번역해 원문과 나란히 보기",
        "텍스트가 작업 중인 앱에 바로 입력",
        "전역 단축키는 어떤 앱에서든 작동",
        "음성 모델 선택: 정확도, 속도, 다운로드 크기를 한눈에",
    ],
    features=[
        "모든 Windows 앱에 받아쓰기 입력",
        "전역 단축키로 시작, 중지, 음소거",
        "마이크, 시스템 오디오 또는 둘 다, 녹음 중 전환",
        "오디오, 텍스트, 타임스탬프, 버전이 담긴 기록",
        "PC에서 화자 구분",
        "받아쓰기, 번역, 채팅 통합 검색",
        "Gemma로 16개 언어 실시간 번역",
        "녹음에 대한 질문과 출처가 있는 답변",
        "TXT, SRT, LRC, Markdown으로 내보내기",
        "Parakeet, Nemotron, Qwen3-ASR 모델, 30개 이상 언어",
        "오프라인 작동: 계정과 클라우드 불필요",
        "버전 1의 설정과 모델 유지",
    ],
    terms=["음성 입력", "음성 텍스트 변환", "받아쓰기", "오프라인 전사", "회의록", "화자 구분", "AI 번역"],
)

L["pt-br"] = dict(
    short="Digitação por voz offline para Windows: fale e o texto aparece em qualquer aplicativo. Cada gravação fica salva com locutores, busca, tradução e respostas de IA no próprio dispositivo. Seu áudio nunca sai do seu PC.",
    description="""O VoiceType transforma sua voz em texto no seu próprio PC. Pressione um atalho, fale, e cada frase concluída é digitada no aplicativo em que você está trabalhando: e-mail, documento, chat, editor de código ou navegador. O reconhecimento de fala e a IA funcionam inteiramente no dispositivo: sem conta, sem nuvem, sem assinatura.

DITE EM QUALQUER APLICATIVO
• Atalhos globais para iniciar, parar e silenciar em qualquer janela (Ctrl+Shift+V por padrão)
• O texto é digitado como teclas pressionadas e a área de transferência não é alterada; há um modo de colar para os aplicativos que precisam
• Microfone, áudio do sistema ou ambos: transcreva chamadas, vídeos e reuniões e troque a fonte durante a gravação

O HISTÓRICO DE CADA GRAVAÇÃO
• Áudio, transcrição e marcas de tempo ficam juntos; reproduza a partir de qualquer frase
• Quem falou e quando: os locutores são identificados no seu PC e podem ser renomeados
• Busca em transcrições, traduções e chats
• Reconheça de novo com outro modelo, edite o texto, mantenha versões e exporte para TXT, SRT, LRC ou Markdown

IA NO DISPOSITIVO COM GEMMA
• Tradução ao vivo para 16 idiomas enquanto você fala ou digita
• Traduza gravações prontas e leia ao lado do original
• Faça perguntas sobre várias gravações e receba respostas com links para o momento exato

ESCOLHA SEU MODELO
• Parakeet TDT v3 (recomendado), Nemotron 3.5 ASR e Qwen3-ASR, em versões completas e compactas
• Mais de 30 idiomas, conforme o modelo, com detecção automática de idioma
• Funciona em qualquer processador moderno; aceleração por GPU opcional em placas de vídeo dedicadas (experimental)

PRIVACIDADE
Áudio, texto e processamento de IA ficam no seu PC. A internet é usada apenas para baixar os modelos que você escolher (do Hugging Face).

ATUALIZANDO DA VERSÃO 1
Suas configurações e modelos baixados são mantidos na primeira inicialização; não é preciso baixar nada de novo.

Requisitos: Windows 10 versão 2004 ou posterior, processador x64, 8 GB de RAM (16 GB recomendados para os recursos do Gemma), de 0,5 a 2,5 GB de disco para um modelo de fala e 2,6 GB para o Gemma.""",
    notes="O VoiceType 2.0 é um aplicativo novo, refeito do zero. Novidades: o Histórico guarda cada gravação com áudio, transcrição, marcas de tempo e versões; os locutores são identificados no seu PC; busca em transcrições, traduções e chats; \"Perguntar ao Gemma\" responde perguntas sobre várias gravações com links para o momento exato; tradução de gravações inteiras ao lado do original; novas Configurações com pastas para modelos, gravações e dados do aplicativo; troca entre microfone, áudio do sistema ou ambos durante a gravação; modelos Parakeet e Nemotron compactos com downloads menores. Suas configurações e modelos baixados são transferidos automaticamente na primeira inicialização: nada precisa ser baixado de novo. Se o seu modelo de tradução estava em outra unidade que os modelos de fala, Configurações > Modelos mostra onde ele está. As regras personalizadas de substituição de texto da versão 1 não estão disponíveis na 2.0.",
    captions=[
        "Cada gravação com transcrição, marcas de tempo e locutores identificados no seu PC",
        "Digite ou dite e o Gemma traduz na hora, no dispositivo",
        "Perguntas sobre várias gravações: as respostas levam ao momento exato",
        "Traduza uma gravação e leia ao lado do original",
        "O texto vai direto para o aplicativo em que você trabalha",
        "Os atalhos globais funcionam em qualquer aplicativo",
        "Escolha o modelo de fala: precisão, velocidade e tamanho de download",
    ],
    features=[
        "Ditado digitado em qualquer aplicativo do Windows",
        "Atalhos globais para iniciar, parar e silenciar",
        "Microfone, áudio do sistema ou ambos, alternáveis durante a gravação",
        "Histórico com áudio, transcrição, marcas de tempo e versões",
        "Identificação de locutores no seu PC",
        "Busca em transcrições, traduções e chats",
        "Tradução ao vivo para 16 idiomas com o Gemma",
        "Perguntas sobre gravações com respostas citadas",
        "Exportação para TXT, SRT, LRC e Markdown",
        "Modelos Parakeet, Nemotron e Qwen3-ASR, mais de 30 idiomas",
        "Funciona offline: sem conta e sem nuvem",
        "Configurações e modelos mantidos da versão 1",
    ],
    terms=["digitação por voz", "voz para texto", "ditado", "transcrição offline", "ata de reunião", "identificação de locutores", "tradução com IA"],
)

L["it-it"] = dict(
    short="Dettatura offline per Windows: parli e il testo appare in qualsiasi app. Ogni registrazione resta salvata con i parlanti, la ricerca, la traduzione e le risposte dell'IA sul dispositivo. L'audio non lascia mai il tuo PC.",
    description="""VoiceType trasforma la tua voce in testo direttamente sul PC. Premi una scorciatoia, parla, e ogni frase completata viene digitata nell'app in cui stai lavorando: e-mail, documento, chat, editor di codice o browser. Il riconoscimento vocale e l'IA funzionano interamente sul dispositivo: nessun account, nessun cloud, nessun abbonamento.

DETTA IN QUALSIASI APP
• Scorciatoie globali per avviare, fermare e silenziare da qualsiasi finestra (Ctrl+Maiusc+V per impostazione predefinita)
• Il testo viene digitato come pressione di tasti e gli appunti restano intatti; per le app che lo richiedono c'è una modalità incolla
• Microfono, audio di sistema o entrambi: trascrivi chiamate, video e riunioni e cambia sorgente durante la registrazione

LA CRONOLOGIA DI OGNI REGISTRAZIONE
• Audio, trascrizione e indicazioni temporali restano insieme; riproduci da qualsiasi frase
• Chi ha parlato e quando: i parlanti vengono riconosciuti sul tuo PC e possono essere rinominati
• Cerca in trascrizioni, traduzioni e chat
• Riconosci di nuovo con un altro modello, modifica il testo, conserva le versioni ed esporta in TXT, SRT, LRC o Markdown

IA SUL DISPOSITIVO CON GEMMA
• Traduzione dal vivo in 16 lingue mentre parli o scrivi
• Traduci le registrazioni e leggile accanto all'originale
• Fai domande su più registrazioni e ottieni risposte con link al momento esatto

SCEGLI IL MODELLO
• Parakeet TDT v3 (consigliato), Nemotron 3.5 ASR e Qwen3-ASR, in versione completa e compatta
• Oltre 30 lingue a seconda del modello, con rilevamento automatico della lingua
• Funziona su qualsiasi CPU moderna; accelerazione GPU facoltativa su schede grafiche dedicate (sperimentale)

PRIVACY
Audio, testo ed elaborazione dell'IA restano sul tuo PC. Internet serve solo per scaricare i modelli scelti (da Hugging Face).

AGGIORNAMENTO DALLA VERSIONE 1
Impostazioni e modelli scaricati vengono mantenuti al primo avvio: non serve scaricare nulla di nuovo.

Requisiti: Windows 10 versione 2004 o successiva, processore x64, 8 GB di RAM (16 GB consigliati per le funzioni di Gemma), da 0,5 a 2,5 GB di spazio per un modello vocale e 2,6 GB per Gemma.""",
    notes="VoiceType 2.0 è una nuova app, ricostruita da zero. Novità: la Cronologia conserva ogni registrazione con audio, trascrizione, indicazioni temporali e versioni; i parlanti vengono riconosciuti sul tuo PC; ricerca in trascrizioni, traduzioni e chat; \"Chiedi a Gemma\" risponde a domande su più registrazioni con link al momento esatto; traduzione di intere registrazioni accanto all'originale; nuove Impostazioni con cartelle per modelli, registrazioni e dati dell'app; passaggio tra microfono, audio di sistema o entrambi durante la registrazione; modelli Parakeet e Nemotron compatti con download più leggeri. Impostazioni e modelli scaricati vengono trasferiti automaticamente al primo avvio: non serve scaricare nulla di nuovo. Se il modello di traduzione si trovava su un'unità diversa da quella dei modelli vocali, Impostazioni > Modelli indica dove si trova. Le regole personalizzate di sostituzione del testo della versione 1 non sono disponibili nella 2.0.",
    captions=[
        "Ogni registrazione con trascrizione, indicazioni temporali e parlanti riconosciuti sul tuo PC",
        "Scrivi o detta e Gemma traduce subito, sul dispositivo",
        "Domande su più registrazioni: le risposte portano al momento esatto",
        "Traduci una registrazione e leggila accanto all'originale",
        "Il testo arriva direttamente nell'app in cui lavori",
        "Le scorciatoie globali funzionano in qualsiasi app",
        "Scegli il modello vocale: precisione, velocità e dimensione a colpo d'occhio",
    ],
    features=[
        "Dettatura digitata in qualsiasi app di Windows",
        "Scorciatoie globali per avviare, fermare e silenziare",
        "Microfono, audio di sistema o entrambi, commutabili in registrazione",
        "Cronologia con audio, trascrizione, indicazioni temporali e versioni",
        "Riconoscimento dei parlanti sul tuo PC",
        "Ricerca in trascrizioni, traduzioni e chat",
        "Traduzione dal vivo in 16 lingue con Gemma",
        "Domande sulle registrazioni con risposte citate",
        "Esportazione in TXT, SRT, LRC e Markdown",
        "Modelli Parakeet, Nemotron e Qwen3-ASR, oltre 30 lingue",
        "Funziona offline: nessun account, nessun cloud",
        "Impostazioni e modelli mantenuti dalla versione 1",
    ],
    terms=["dettatura vocale", "voce in testo", "dettatura", "trascrizione offline", "verbale riunione", "riconoscimento parlanti", "traduzione IA"],
)

L["ar-sa"] = dict(
    short="كتابة صوتية دون اتصال لنظام Windows: تحدّث فيظهر النص في أي تطبيق. يُحفظ كل تسجيل مع تمييز المتحدثين والبحث والترجمة وإجابات الذكاء الاصطناعي على الجهاز نفسه. لا يغادر الصوت جهازك أبدًا.",
    description="""يحوّل VoiceType صوتك إلى نص على جهاز الكمبيوتر مباشرة. اضغط اختصارًا وتحدّث، فتُكتب كل جملة مكتملة في التطبيق الذي تعمل فيه: البريد أو المستند أو الدردشة أو محرر التعليمات البرمجية أو المتصفح. يعمل التعرف على الكلام والذكاء الاصطناعي على جهازك بالكامل: بلا حساب ولا سحابة ولا اشتراك.

إملاء في أي تطبيق
• اختصارات عامة للبدء والإيقاف وكتم الصوت من أي نافذة (Ctrl+Shift+V افتراضيًا)
• يُكتب النص كضغطات مفاتيح فلا تتغير الحافظة، ويتوفر وضع اللصق للتطبيقات التي تحتاجه
• الميكروفون أو صوت النظام أو كلاهما: فرّغ المكالمات والفيديوهات والاجتماعات وبدّل المصدر أثناء التسجيل

سجل لكل تسجيل
• يُحفظ الصوت والنص والطوابع الزمنية معًا، مع التشغيل من أي جملة
• من تحدّث ومتى: يُميَّز المتحدثون على جهازك ويمكن إعادة تسميتهم
• البحث في النصوص والترجمات والمحادثات
• إعادة التعرف بنموذج آخر وتحرير النص والاحتفاظ بالإصدارات والتصدير إلى TXT وSRT وLRC وMarkdown

ذكاء اصطناعي على الجهاز مع GEMMA
• ترجمة فورية إلى 16 لغة أثناء التحدث أو الكتابة
• ترجمة التسجيلات المكتملة وقراءتها بجوار النص الأصلي
• اطرح أسئلة عن عدة تسجيلات واحصل على إجابات مع روابط إلى اللحظة المحددة

اختر النموذج
• Parakeet TDT v3 (موصى به) وNemotron 3.5 ASR وQwen3-ASR بإصدارات كاملة ومدمجة
• أكثر من 30 لغة حسب النموذج مع اكتشاف اللغة تلقائيًا
• يعمل على أي معالج حديث، مع تسريع اختياري بوحدة معالجة الرسومات على بطاقات الرسومات المنفصلة (تجريبي)

الخصوصية
يبقى الصوت والنص ومعالجة الذكاء الاصطناعي على جهازك. يُستخدم الإنترنت فقط لتنزيل النماذج التي تختارها (من Hugging Face).

التحديث من الإصدار 1
تُنقل إعداداتك ونماذجك المنزّلة عند أول تشغيل، فلا حاجة إلى تنزيل أي شيء من جديد.

المتطلبات: Windows 10 الإصدار 2004 أو أحدث، معالج x64، ذاكرة 8 غيغابايت (يوصى بـ 16 غيغابايت لميزات Gemma)، ومن 0.5 إلى 2.5 غيغابايت لنموذج الكلام و2.6 غيغابايت لـ Gemma.""",
    notes="VoiceType 2.0 تطبيق جديد أُعيد بناؤه بالكامل. الجديد: يحفظ السجل كل تسجيل مع الصوت والنص والطوابع الزمنية والإصدارات؛ تمييز المتحدثين على جهازك؛ البحث في النصوص والترجمات والمحادثات؛ ميزة \"اسأل Gemma\" تجيب عن أسئلة حول عدة تسجيلات مع روابط إلى اللحظة المحددة؛ ترجمة التسجيلات كاملة بجوار الأصل؛ إعدادات جديدة لاختيار مجلدات النماذج والتسجيلات وبيانات التطبيق؛ التبديل بين الميكروفون وصوت النظام أو كليهما أثناء التسجيل؛ نماذج Parakeet وNemotron مدمجة بحجم تنزيل أصغر. تُنقل إعداداتك ونماذجك المنزّلة تلقائيًا عند أول تشغيل، فلا حاجة إلى تنزيل أي شيء من جديد. إذا كان نموذج الترجمة على محرك أقراص غير محرك نماذج الكلام، فستعرض الإعدادات > النماذج مكانه. قواعد استبدال النص المخصصة من الإصدار 1 غير متوفرة في 2.0.",
    captions=[
        "كل تسجيل مع النص والطوابع الزمنية والمتحدثين المميَّزين على جهازك",
        "اكتب أو أملِ، ويترجم Gemma فورًا على الجهاز",
        "أسئلة عن عدة تسجيلات: الإجابات تقودك إلى اللحظة المحددة",
        "ترجم تسجيلًا واقرأه بجوار الأصل",
        "يصل النص مباشرة إلى التطبيق الذي تعمل فيه",
        "الاختصارات العامة تعمل في أي تطبيق",
        "اختر نموذج الكلام: الدقة والسرعة وحجم التنزيل بنظرة واحدة",
    ],
    features=[
        "إملاء يُكتب في أي تطبيق Windows",
        "اختصارات عامة للبدء والإيقاف وكتم الصوت",
        "الميكروفون أو صوت النظام أو كلاهما مع التبديل أثناء التسجيل",
        "سجل يضم الصوت والنص والطوابع الزمنية والإصدارات",
        "تمييز المتحدثين على جهازك",
        "البحث في النصوص والترجمات والمحادثات",
        "ترجمة فورية إلى 16 لغة مع Gemma",
        "أسئلة عن التسجيلات مع إجابات موثقة",
        "التصدير إلى TXT وSRT وLRC وMarkdown",
        "نماذج Parakeet وNemotron وQwen3-ASR بأكثر من 30 لغة",
        "يعمل دون اتصال: بلا حساب ولا سحابة",
        "نقل الإعدادات والنماذج من الإصدار 1",
    ],
    terms=["الكتابة بالصوت", "تحويل الكلام إلى نص", "إملاء", "تفريغ صوتي", "محضر اجتماع", "تمييز المتحدثين", "ترجمة بالذكاء الاصطناعي"],
)

L["hi-in"] = dict(
    short="Windows के लिए ऑफ़लाइन वॉइस टाइपिंग: बोलिए और टेक्स्ट किसी भी ऐप में आ जाता है। हर रिकॉर्डिंग वक्ता पहचान, खोज, AI अनुवाद और उत्तरों के साथ डिवाइस पर सहेजी जाती है। आपका ऑडियो कभी आपके PC से बाहर नहीं जाता।",
    description="""VoiceType आपकी आवाज़ को सीधे आपके PC पर टेक्स्ट में बदलता है। शॉर्टकट दबाइए, बोलिए, और हर पूरा वाक्य उसी ऐप में टाइप हो जाता है जिसमें आप काम कर रहे हैं: ईमेल, दस्तावेज़, चैट, कोड एडिटर या ब्राउज़र। वाक् पहचान और AI पूरी तरह आपके डिवाइस पर चलते हैं: न खाता, न क्लाउड, न सदस्यता।

किसी भी ऐप में बोलकर लिखें
• वैश्विक शॉर्टकट से किसी भी विंडो से शुरू, बंद और म्यूट करें (डिफ़ॉल्ट Ctrl+Shift+V)
• टेक्स्ट कुंजी दबाने की तरह टाइप होता है, क्लिपबोर्ड नहीं बदलता; ज़रूरत वाले ऐप के लिए पेस्ट मोड भी है
• माइक्रोफ़ोन, सिस्टम ऑडियो या दोनों: कॉल, वीडियो और मीटिंग का ट्रांसक्रिप्शन, रिकॉर्डिंग के दौरान स्रोत बदलें

हर रिकॉर्डिंग का इतिहास
• ऑडियो, टेक्स्ट और टाइमस्टैम्प एक साथ सहेजे जाते हैं; किसी भी वाक्य से चलाएँ
• किसने कब बोला: वक्ताओं की पहचान आपके PC पर होती है और उनका नाम बदला जा सकता है
• ट्रांसक्रिप्ट, अनुवाद और चैट में खोज
• दूसरे मॉडल से फिर से पहचानें, टेक्स्ट संपादित करें, संस्करण रखें, TXT, SRT, LRC या Markdown में निर्यात करें

डिवाइस पर AI: GEMMA
• बोलते या टाइप करते समय 16 भाषाओं में लाइव अनुवाद
• पूरी हो चुकी रिकॉर्डिंग का अनुवाद, मूल पाठ के साथ
• कई रिकॉर्डिंग के बारे में प्रश्न पूछें और सटीक पल के लिंक वाले उत्तर पाएँ

अपना मॉडल चुनें
• Parakeet TDT v3 (अनुशंसित), Nemotron 3.5 ASR और Qwen3-ASR, पूर्ण और कॉम्पैक्ट आकार में
• मॉडल के अनुसार 30 से अधिक भाषाएँ, भाषा की स्वचालित पहचान
• किसी भी आधुनिक CPU पर चलता है; अलग ग्राफ़िक्स कार्ड पर वैकल्पिक GPU त्वरण (प्रायोगिक)

गोपनीयता
ऑडियो, टेक्स्ट और AI प्रोसेसिंग आपके PC पर ही रहते हैं। इंटरनेट का उपयोग केवल आपके चुने मॉडल (Hugging Face से) डाउनलोड करने के लिए होता है।

संस्करण 1 से अपडेट
आपकी सेटिंग और डाउनलोड किए गए मॉडल पहली बार शुरू होने पर अपने आप साथ आ जाते हैं, कुछ भी दोबारा डाउनलोड नहीं करना पड़ता।

आवश्यकताएँ: Windows 10 संस्करण 2004 या बाद का, x64 प्रोसेसर, 8 GB RAM (Gemma सुविधाओं के लिए 16 GB अनुशंसित), वाक् मॉडल के लिए 0.5 से 2.5 GB और Gemma के लिए 2.6 GB डिस्क स्थान।""",
    notes="VoiceType 2.0 एक नया ऐप है, जिसे शुरू से फिर बनाया गया है। नया: इतिहास हर रिकॉर्डिंग को ऑडियो, टेक्स्ट, टाइमस्टैम्प और संस्करणों के साथ सहेजता है; वक्ताओं की पहचान आपके PC पर; ट्रांसक्रिप्ट, अनुवाद और चैट में खोज; \"Gemma से पूछें\" कई रिकॉर्डिंग के बारे में प्रश्नों के उत्तर सटीक पल के लिंक के साथ देता है; पूरी रिकॉर्डिंग का अनुवाद मूल पाठ के साथ; मॉडल, रिकॉर्डिंग और ऐप डेटा के फ़ोल्डर चुनने वाली नई सेटिंग; रिकॉर्डिंग के दौरान माइक्रोफ़ोन, सिस्टम ऑडियो या दोनों के बीच बदलाव; छोटे डाउनलोड वाले कॉम्पैक्ट Parakeet और Nemotron मॉडल। आपकी सेटिंग और डाउनलोड किए गए मॉडल पहली बार शुरू होने पर अपने आप साथ आ जाते हैं: कुछ भी दोबारा डाउनलोड नहीं करना पड़ता। अगर अनुवाद मॉडल वाक् मॉडल से अलग ड्राइव पर था, तो सेटिंग > मॉडल में उसका स्थान दिखता है। संस्करण 1 के कस्टम टेक्स्ट बदलने के नियम 2.0 में उपलब्ध नहीं हैं।",
    captions=[
        "हर रिकॉर्डिंग टेक्स्ट, टाइमस्टैम्प और आपके PC पर पहचाने गए वक्ताओं के साथ",
        "टाइप करें या बोलें, Gemma डिवाइस पर तुरंत अनुवाद करता है",
        "कई रिकॉर्डिंग पर प्रश्न: उत्तर सटीक पल तक ले जाते हैं",
        "रिकॉर्डिंग का अनुवाद करें और मूल के साथ पढ़ें",
        "टेक्स्ट सीधे उसी ऐप में जाता है जिसमें आप काम कर रहे हैं",
        "वैश्विक शॉर्टकट हर ऐप में काम करते हैं",
        "वाक् मॉडल चुनें: सटीकता, गति और डाउनलोड आकार एक नज़र में",
    ],
    features=[
        "किसी भी Windows ऐप में बोलकर टाइप करें",
        "शुरू, बंद और म्यूट करने के लिए वैश्विक शॉर्टकट",
        "माइक्रोफ़ोन, सिस्टम ऑडियो या दोनों, रिकॉर्डिंग के दौरान बदलें",
        "ऑडियो, टेक्स्ट, टाइमस्टैम्प और संस्करणों वाला इतिहास",
        "आपके PC पर वक्ता पहचान",
        "ट्रांसक्रिप्ट, अनुवाद और चैट में खोज",
        "Gemma के साथ 16 भाषाओं में लाइव अनुवाद",
        "रिकॉर्डिंग पर प्रश्न, स्रोत सहित उत्तर",
        "TXT, SRT, LRC और Markdown में निर्यात",
        "Parakeet, Nemotron और Qwen3-ASR मॉडल, 30+ भाषाएँ",
        "ऑफ़लाइन काम करता है: न खाता, न क्लाउड",
        "संस्करण 1 की सेटिंग और मॉडल साथ आते हैं",
    ],
    terms=["वॉइस टाइपिंग", "स्पीच टू टेक्स्ट", "डिक्टेशन", "ऑफ़लाइन ट्रांसक्रिप्शन", "मीटिंग नोट्स", "वक्ता पहचान", "AI अनुवाद"],
)

L["tr-tr"] = dict(
    short="Windows için çevrimdışı sesle yazma: konuşun, metin her uygulamada görünsün. Her kayıt; konuşmacılar, arama, yapay zekâ çevirisi ve yanıtlarıyla cihazda saklanır. Sesiniz bilgisayarınızdan asla çıkmaz.",
    description="""VoiceType sesinizi doğrudan bilgisayarınızda metne dönüştürür. Bir kısayola basın, konuşun; biten her cümle çalıştığınız uygulamaya yazılır: e-posta, belge, sohbet, kod düzenleyici veya tarayıcı. Konuşma tanıma ve yapay zekâ tamamen cihazınızda çalışır: hesap yok, bulut yok, abonelik yok.

HER UYGULAMADA DİKTE
• Genel kısayollarla her pencereden başlatın, durdurun ve sesi kapatın (varsayılan Ctrl+Shift+V)
• Metin tuş vuruşu olarak yazılır, pano değişmez; gereken uygulamalar için yapıştırma modu da var
• Mikrofon, sistem sesi veya ikisi birden: aramaları, videoları ve toplantıları yazıya dökün, kayıt sırasında kaynağı değiştirin

HER KAYDIN GEÇMİŞİ
• Ses, metin ve zaman damgaları birlikte saklanır; istediğiniz cümleden oynatın
• Kim ne zaman konuştu: konuşmacılar bilgisayarınızda ayırt edilir ve yeniden adlandırılabilir
• Dökümler, çeviriler ve sohbetlerde arama
• Başka bir modelle yeniden tanıma, metni düzenleme, sürümleri saklama, TXT, SRT, LRC veya Markdown olarak dışa aktarma

CİHAZDA YAPAY ZEKÂ: GEMMA
• Konuşurken veya yazarken 16 dile canlı çeviri
• Tamamlanan kayıtları çevirin ve orijinalin yanında okuyun
• Birden fazla kayıt hakkında soru sorun, tam ana bağlantı veren yanıtlar alın

MODELİNİZİ SEÇİN
• Parakeet TDT v3 (önerilen), Nemotron 3.5 ASR ve Qwen3-ASR; tam ve kompakt boyutlarda
• Modele göre 30'dan fazla dil, otomatik dil algılama
• Her modern işlemcide çalışır; harici ekran kartlarında isteğe bağlı GPU hızlandırma (deneysel)

GİZLİLİK
Ses, metin ve yapay zekâ işlemleri bilgisayarınızda kalır. İnternet yalnızca seçtiğiniz modelleri (Hugging Face'ten) indirmek için kullanılır.

SÜRÜM 1'DEN GÜNCELLEME
Ayarlarınız ve indirdiğiniz modeller ilk açılışta aktarılır, hiçbir şeyi yeniden indirmeniz gerekmez.

Gereksinimler: Windows 10 sürüm 2004 veya üzeri, x64 işlemci, 8 GB RAM (Gemma özellikleri için 16 GB önerilir), konuşma modeli için 0,5–2,5 GB ve Gemma için 2,6 GB disk alanı.""",
    notes="VoiceType 2.0, baştan yeniden geliştirilmiş yeni bir uygulamadır. Yenilikler: Geçmiş her kaydı sesi, metni, zaman damgaları ve sürümleriyle saklar; konuşmacılar bilgisayarınızda ayırt edilir; dökümler, çeviriler ve sohbetlerde arama; \"Gemma'ya sor\" birden fazla kayıt hakkındaki soruları tam ana bağlantılarla yanıtlar; kayıtların tamamı orijinalin yanında çevrilir; modeller, kayıtlar ve uygulama verileri için klasör seçilebilen yeni Ayarlar; kayıt sırasında mikrofon, sistem sesi veya ikisi arasında geçiş; daha küçük indirmeli kompakt Parakeet ve Nemotron modelleri. Ayarlarınız ve indirdiğiniz modeller ilk açılışta otomatik olarak aktarılır: hiçbir şeyi yeniden indirmeniz gerekmez. Çeviri modeliniz konuşma modellerinden farklı bir sürücüdeyse Ayarlar > Modeller nerede olduğunu gösterir. Sürüm 1'deki özel metin değiştirme kuralları 2.0'da kullanılamaz.",
    captions=[
        "Her kayıt; metni, zaman damgaları ve bilgisayarınızda ayırt edilen konuşmacılarla",
        "Yazın ya da dikte edin, Gemma cihazda anında çevirsin",
        "Birden fazla kayda soru: yanıtlar tam ana götürür",
        "Bir kaydı çevirin ve orijinalin yanında okuyun",
        "Metin doğrudan çalıştığınız uygulamaya gider",
        "Genel kısayollar her uygulamada çalışır",
        "Konuşma modelini seçin: doğruluk, hız ve indirme boyutu bir bakışta",
    ],
    features=[
        "Her Windows uygulamasına yazılan dikte",
        "Başlatma, durdurma ve ses kapatma için genel kısayollar",
        "Mikrofon, sistem sesi veya ikisi; kayıt sırasında değiştirilebilir",
        "Ses, metin, zaman damgaları ve sürümlerle geçmiş",
        "Bilgisayarınızda konuşmacı ayrımı",
        "Dökümler, çeviriler ve sohbetlerde arama",
        "Gemma ile 16 dile canlı çeviri",
        "Kayıtlar hakkında kaynaklı yanıtlar",
        "TXT, SRT, LRC ve Markdown olarak dışa aktarma",
        "Parakeet, Nemotron ve Qwen3-ASR modelleri, 30+ dil",
        "Çevrimdışı çalışır: hesap yok, bulut yok",
        "Sürüm 1'in ayarları ve modelleri aktarılır",
    ],
    terms=["sesle yazma", "konuşmayı metne çevirme", "dikte", "çevrimdışı döküm", "toplantı notu", "konuşmacı ayrımı", "yapay zeka çeviri"],
)

L["uk-ua"] = dict(
    short="Голосове введення без інтернету для Windows: говоріть, і текст з'являється в будь-якому застосунку. Кожен запис зберігається з мовцями, пошуком, перекладом і відповідями ШІ на самому пристрої. Аудіо ніколи не залишає ваш комп'ютер.",
    description="""VoiceType перетворює голос на текст просто на вашому комп'ютері. Натисніть гарячу клавішу, говоріть — і кожна завершена фраза друкується в застосунок, у якому ви працюєте: пошту, документ, чат, редактор коду чи браузер. Розпізнавання мовлення та ШІ працюють повністю на пристрої: без облікового запису, без хмари, без підписки.

ДИКТУВАННЯ В БУДЬ-ЯКИЙ ЗАСТОСУНОК
• Глобальні гарячі клавіші: старт, стоп і вимкнення мікрофона з будь-якого вікна (типово Ctrl+Shift+V)
• Текст вводиться натисканнями клавіш, буфер обміну не змінюється; для особливих застосунків є режим вставлення
• Мікрофон, звук системи або обидва: розшифровка дзвінків, відео та зустрічей, джерело можна змінити під час запису

ІСТОРІЯ КОЖНОГО ЗАПИСУ
• Аудіо, текст і позначки часу зберігаються разом; відтворення з будь-якої фрази
• Хто й коли говорив: мовців розпізнано на вашому комп'ютері, їх можна перейменувати
• Пошук у розшифровках, перекладах і чатах
• Повторне розпізнавання іншою моделлю, редагування тексту, версії, експорт у TXT, SRT, LRC і Markdown

ШІ НА ПРИСТРОЇ: GEMMA
• Живий переклад 16 мовами, поки ви говорите чи друкуєте
• Переклад готових записів поруч з оригіналом
• Запитання до кількох записів: відповіді з посиланнями на потрібний момент

ВИБІР МОДЕЛІ
• Parakeet TDT v3 (рекомендовано), Nemotron 3.5 ASR і Qwen3-ASR, у повних і компактних версіях
• Понад 30 мов залежно від моделі, автовизначення мови
• Працює на будь-якому сучасному процесорі; прискорення на дискретній відеокарті (експериментально)

КОНФІДЕНЦІЙНІСТЬ
Аудіо, текст і робота ШІ залишаються на вашому комп'ютері. Інтернет потрібен лише для завантаження вибраних моделей (з Hugging Face).

ОНОВЛЕННЯ З ВЕРСІЇ 1
Налаштування та завантажені моделі переносяться під час першого запуску, нічого не потрібно завантажувати знову.

Вимоги: Windows 10 версії 2004 або новішої, процесор x64, 8 ГБ ОЗП (16 ГБ для функцій Gemma), від 0,5 до 2,5 ГБ на диску для моделі розпізнавання і 2,6 ГБ для Gemma.""",
    notes="VoiceType 2.0 — новий застосунок, створений з нуля. Нове: Історія зберігає кожен запис з аудіо, текстом, позначками часу та версіями; мовців розпізнано на вашому комп'ютері; пошук у розшифровках, перекладах і чатах; «Запитати Gemma» відповідає на запитання до кількох записів із посиланнями на потрібний момент; переклад цілих записів поруч з оригіналом; нові Налаштування з папками для моделей, записів і даних застосунку; перемикання між мікрофоном, звуком системи або обома під час запису; компактні моделі Parakeet і Nemotron з меншим обсягом завантаження. Налаштування та завантажені моделі переносяться автоматично під час першого запуску — нічого не потрібно завантажувати знову. Якщо модель перекладу була на іншому диску, ніж моделі розпізнавання, розділ «Налаштування > Моделі» покаже, де вона. Власні правила заміни тексту з версії 1 у 2.0 недоступні.",
    captions=[
        "Кожен запис із текстом, позначками часу та мовцями, розпізнаними на вашому комп'ютері",
        "Друкуйте чи диктуйте — Gemma перекладає на льоту, просто на пристрої",
        "Запитання до кількох записів: відповіді ведуть до потрібного моменту",
        "Переклад запису поруч з оригіналом",
        "Текст одразу потрапляє в застосунок, у якому ви працюєте",
        "Глобальні гарячі клавіші працюють у будь-якому застосунку",
        "Вибір моделі розпізнавання: точність, швидкість і розмір завантаження",
    ],
    features=[
        "Диктування з введенням тексту в будь-який застосунок Windows",
        "Глобальні гарячі клавіші: старт, стоп, вимкнення мікрофона",
        "Мікрофон, звук системи або обидва, перемикання під час запису",
        "Історія з аудіо, текстом, позначками часу та версіями",
        "Розпізнавання мовців на вашому комп'ютері",
        "Пошук у розшифровках, перекладах і чатах",
        "Живий переклад 16 мовами з Gemma",
        "Запитання до записів з відповідями з посиланнями",
        "Експорт у TXT, SRT, LRC і Markdown",
        "Моделі Parakeet, Nemotron і Qwen3-ASR, понад 30 мов",
        "Працює без інтернету: без облікового запису й хмари",
        "Налаштування й моделі переносяться з версії 1",
    ],
    terms=["голосове введення", "розпізнавання мовлення", "диктування", "розшифровка аудіо", "мовлення в текст", "протокол зустрічі", "переклад ШІ"],
)

L["pl-pl"] = dict(
    short="Pisanie głosem offline w systemie Windows: mów, a tekst pojawi się w dowolnej aplikacji. Każde nagranie jest zapisywane z rozmówcami, wyszukiwaniem, tłumaczeniem i odpowiedziami AI na urządzeniu. Dźwięk nigdy nie opuszcza Twojego komputera.",
    description="""VoiceType zamienia głos na tekst bezpośrednio na Twoim komputerze. Naciśnij skrót, mów, a każde zakończone zdanie zostanie wpisane do aplikacji, w której pracujesz: poczty, dokumentu, czatu, edytora kodu lub przeglądarki. Rozpoznawanie mowy i AI działają w całości na urządzeniu: bez konta, bez chmury, bez subskrypcji.

DYKTUJ DO DOWOLNEJ APLIKACJI
• Globalne skróty do rozpoczynania, zatrzymywania i wyciszania z dowolnego okna (domyślnie Ctrl+Shift+V)
• Tekst jest wpisywany jako naciśnięcia klawiszy, schowek pozostaje nietknięty; dla aplikacji, które tego wymagają, jest tryb wklejania
• Mikrofon, dźwięk systemu lub oba: transkrybuj rozmowy, filmy i spotkania, a źródło zmieniaj w trakcie nagrywania

HISTORIA KAŻDEGO NAGRANIA
• Dźwięk, transkrypcja i znaczniki czasu są zapisywane razem; odtwarzanie od dowolnego zdania
• Kto mówił i kiedy: rozmówcy są rozpoznawani na Twoim komputerze i można zmienić ich nazwy
• Wyszukiwanie w transkrypcjach, tłumaczeniach i czatach
• Ponowne rozpoznanie innym modelem, edycja tekstu, wersje, eksport do TXT, SRT, LRC lub Markdown

AI NA URZĄDZENIU: GEMMA
• Tłumaczenie na żywo na 16 języków podczas mówienia lub pisania
• Tłumaczenie gotowych nagrań obok oryginału
• Pytania dotyczące wielu nagrań i odpowiedzi z linkami do dokładnego momentu

WYBIERZ MODEL
• Parakeet TDT v3 (zalecany), Nemotron 3.5 ASR i Qwen3-ASR w wersjach pełnych i kompaktowych
• Ponad 30 języków zależnie od modelu, z automatycznym wykrywaniem języka
• Działa na każdym nowoczesnym procesorze; opcjonalne przyspieszenie GPU na dedykowanych kartach graficznych (eksperymentalne)

PRYWATNOŚĆ
Dźwięk, tekst i przetwarzanie AI pozostają na Twoim komputerze. Internet służy tylko do pobrania wybranych modeli (z Hugging Face).

AKTUALIZACJA Z WERSJI 1
Ustawienia i pobrane modele są przenoszone przy pierwszym uruchomieniu, nie trzeba niczego pobierać ponownie.

Wymagania: Windows 10 w wersji 2004 lub nowszej, procesor x64, 8 GB RAM (16 GB zalecane dla funkcji Gemma), od 0,5 do 2,5 GB miejsca na model mowy i 2,6 GB na Gemma.""",
    notes="VoiceType 2.0 to nowa aplikacja, zbudowana od podstaw. Nowości: Historia zapisuje każde nagranie z dźwiękiem, transkrypcją, znacznikami czasu i wersjami; rozmówcy są rozpoznawani na Twoim komputerze; wyszukiwanie w transkrypcjach, tłumaczeniach i czatach; „Zapytaj Gemma” odpowiada na pytania dotyczące wielu nagrań z linkami do dokładnego momentu; tłumaczenie całych nagrań obok oryginału; nowe Ustawienia z folderami na modele, nagrania i dane aplikacji; przełączanie między mikrofonem, dźwiękiem systemu lub oboma w trakcie nagrywania; kompaktowe modele Parakeet i Nemotron o mniejszym rozmiarze. Ustawienia i pobrane modele są przenoszone automatycznie przy pierwszym uruchomieniu: nic nie trzeba pobierać ponownie. Jeśli model tłumaczenia był na innym dysku niż modele mowy, Ustawienia > Modele pokażą, gdzie się znajduje. Własne reguły zamiany tekstu z wersji 1 nie są dostępne w 2.0.",
    captions=[
        "Każde nagranie z transkrypcją, znacznikami czasu i rozmówcami rozpoznanymi na Twoim komputerze",
        "Pisz lub dyktuj, a Gemma tłumaczy na bieżąco, na urządzeniu",
        "Pytania do wielu nagrań: odpowiedzi prowadzą do dokładnego momentu",
        "Przetłumacz nagranie i czytaj je obok oryginału",
        "Tekst trafia prosto do aplikacji, w której pracujesz",
        "Globalne skróty działają w każdej aplikacji",
        "Wybierz model mowy: dokładność, szybkość i rozmiar w jednym miejscu",
    ],
    features=[
        "Dyktowanie wpisywane do dowolnej aplikacji Windows",
        "Globalne skróty do startu, zatrzymania i wyciszenia",
        "Mikrofon, dźwięk systemu lub oba, przełączane w trakcie nagrywania",
        "Historia z dźwiękiem, transkrypcją, znacznikami czasu i wersjami",
        "Rozpoznawanie rozmówców na Twoim komputerze",
        "Wyszukiwanie w transkrypcjach, tłumaczeniach i czatach",
        "Tłumaczenie na żywo na 16 języków z Gemma",
        "Pytania do nagrań z odpowiedziami ze źródłami",
        "Eksport do TXT, SRT, LRC i Markdown",
        "Modele Parakeet, Nemotron i Qwen3-ASR, ponad 30 języków",
        "Działa offline: bez konta i bez chmury",
        "Ustawienia i modele przeniesione z wersji 1",
    ],
    terms=["pisanie głosem", "mowa na tekst", "dyktowanie", "transkrypcja offline", "notatki ze spotkań", "rozpoznawanie mówców", "tłumaczenie AI"],
)

L["nl-nl"] = dict(
    short="Offline spraakinvoer voor Windows: spreek, en de tekst verschijnt in elke app. Elke opname wordt bewaard met sprekers, zoeken en AI-vertaling en -antwoorden op het apparaat. Je audio verlaat je pc nooit.",
    description="""VoiceType zet je stem direct op je pc om in tekst. Druk op een sneltoets, spreek, en elke afgeronde zin wordt getypt in de app waarin je werkt: e-mail, document, chat, code-editor of browser. Spraakherkenning en AI draaien volledig op je apparaat: geen account, geen cloud, geen abonnement.

DICTEREN IN ELKE APP
• Globale sneltoetsen om vanuit elk venster te starten, te stoppen en te dempen (standaard Ctrl+Shift+V)
• Tekst wordt als toetsaanslagen getypt, je klembord blijft onaangeroerd; voor apps die het nodig hebben is er een plakmodus
• Microfoon, systeemgeluid of allebei: transcribeer gesprekken, video's en vergaderingen en wissel de bron tijdens het opnemen

EEN GESCHIEDENIS VAN ELKE OPNAME
• Audio, transcript en tijdstempels blijven samen; afspelen vanaf elke zin
• Wie sprak wanneer: sprekers worden op je pc herkend en kunnen een naam krijgen
• Zoeken in transcripten, vertalingen en chats
• Opnieuw herkennen met een ander model, tekst bewerken, versies bewaren, exporteren naar TXT, SRT, LRC of Markdown

AI OP HET APPARAAT MET GEMMA
• Live vertaling naar 16 talen terwijl je spreekt of typt
• Vertaal afgeronde opnamen en lees ze naast het origineel
• Stel vragen over meerdere opnamen en krijg antwoorden met links naar het exacte moment

KIES JE MODEL
• Parakeet TDT v3 (aanbevolen), Nemotron 3.5 ASR en Qwen3-ASR, in volledige en compacte versies
• Meer dan 30 talen, afhankelijk van het model, met automatische taaldetectie
• Werkt op elke moderne processor; optionele GPU-versnelling op losse videokaarten (experimenteel)

PRIVACY
Audio, tekst en AI-verwerking blijven op je pc. Internet wordt alleen gebruikt om de modellen te downloaden die je kiest (van Hugging Face).

BIJWERKEN VANAF VERSIE 1
Je instellingen en gedownloade modellen worden bij de eerste start overgenomen; je hoeft niets opnieuw te downloaden.

Vereisten: Windows 10 versie 2004 of later, x64-processor, 8 GB RAM (16 GB aanbevolen voor de Gemma-functies), 0,5 tot 2,5 GB schijfruimte voor een spraakmodel en 2,6 GB voor Gemma.""",
    notes="VoiceType 2.0 is een nieuwe app, helemaal opnieuw gebouwd. Nieuw: Geschiedenis bewaart elke opname met audio, transcript, tijdstempels en versies; sprekers worden op je pc herkend; zoeken in transcripten, vertalingen en chats; 'Vraag het Gemma' beantwoordt vragen over meerdere opnamen met links naar het exacte moment; hele opnamen vertalen naast het origineel; nieuwe Instellingen met mappen voor modellen, opnamen en app-gegevens; wisselen tussen microfoon, systeemgeluid of allebei tijdens het opnemen; compacte Parakeet- en Nemotron-modellen met kleinere downloads. Je instellingen en gedownloade modellen gaan bij de eerste start automatisch mee: je hoeft niets opnieuw te downloaden. Stond je vertaalmodel op een ander station dan je spraakmodellen, dan toont Instellingen > Modellen waar het staat. Eigen regels voor tekstvervanging uit versie 1 zijn niet beschikbaar in 2.0.",
    captions=[
        "Elke opname met transcript, tijdstempels en sprekers, herkend op je pc",
        "Typ of dicteer, en Gemma vertaalt meteen, op het apparaat",
        "Vragen over meerdere opnamen: antwoorden leiden naar het exacte moment",
        "Vertaal een opname en lees die naast het origineel",
        "Tekst komt direct in de app waarin je werkt",
        "Globale sneltoetsen werken in elke app",
        "Kies het spraakmodel: nauwkeurigheid, snelheid en downloadgrootte in één oogopslag",
    ],
    features=[
        "Dicteren rechtstreeks in elke Windows-app",
        "Globale sneltoetsen om te starten, te stoppen en te dempen",
        "Microfoon, systeemgeluid of allebei, wisselbaar tijdens het opnemen",
        "Geschiedenis met audio, transcript, tijdstempels en versies",
        "Sprekerherkenning op je pc",
        "Zoeken in transcripten, vertalingen en chats",
        "Live vertaling naar 16 talen met Gemma",
        "Vragen over opnamen met onderbouwde antwoorden",
        "Exporteren naar TXT, SRT, LRC en Markdown",
        "Parakeet-, Nemotron- en Qwen3-ASR-modellen, 30+ talen",
        "Werkt offline: geen account, geen cloud",
        "Instellingen en modellen uit versie 1 blijven behouden",
    ],
    terms=["spraakinvoer", "spraak naar tekst", "dicteren", "offline transcriptie", "vergadernotulen", "sprekerherkenning", "AI vertaling"],
)


def check():
    assert set(L) == set(LANGS), set(LANGS) ^ set(L)
    for lang, d in L.items():
        assert len(d["short"]) <= 1000, (lang, "short")
        assert len(d["description"]) <= 10000, (lang, "description")
        assert len(d["notes"]) <= 1500, (lang, "notes", len(d["notes"]))
        assert len(d["captions"]) == len(SCREENSHOTS), (lang, "captions")
        assert all(len(c) <= 200 for c in d["captions"]), (lang, "caption length")
        assert len(d["features"]) <= MAX_FEATURES and all(len(f) <= 200 for f in d["features"]), (lang, "features")
        assert len(d["terms"]) == 7 and all(len(t) <= 30 for t in d["terms"]), (lang, "terms")
        assert sum(len(t.split()) for t in d["terms"]) <= 21, (lang, "search term words")
    for shot in SCREENSHOTS:
        assert (HERE / shot).exists(), shot
    assert [p.name for p in HERE.glob("*.csv")] in ([], [OUT_NAME]), "the upload folder may hold one CSV only"


def rows():
    def row(field, fid, kind, values):
        return [field, str(fid), kind, ""] + values

    yield row("Title", 4, TEXT, ["VoiceType"] * len(LANGS))
    yield row("ShortDescription", 8, TEXT, [L[l]["short"] for l in LANGS])
    yield row("Description", 2, TEXT, [L[l]["description"] for l in LANGS])
    yield row("ReleaseNotes", 3, TEXT, [L[l]["notes"] for l in LANGS])
    for i, shot in enumerate(SCREENSHOTS):
        yield row(f"DesktopScreenshot{i + 1}", 100 + i, PATH, [f"{HERE.name}/{shot}"] * len(LANGS))
    for i in range(len(SCREENSHOTS)):
        yield row(f"DesktopScreenshotCaption{i + 1}", 150 + i, TEXT, [L[l]["captions"][i] for l in LANGS])
    # Version 1 listed 14 features: the rows up to 20 are written (empty ones clear the old text).
    for i in range(MAX_FEATURES):
        yield row(f"Feature{i + 1}", 700 + i, TEXT, [L[l]["features"][i] if i < len(L[l]["features"]) else "" for l in LANGS])
    for i in range(7):
        yield row(f"SearchTerm{i + 1}", 900 + i, TEXT, [L[l]["terms"][i] for l in LANGS])


def fill_export(export_path, texts_only=False):
    """Partner Center only imports a file whose language columns match the submission: fill a fresh export of it.

    The Uno package declares the language "en" (not "en-us" as WinUI did), so the submission has an extra "en" listing;
    every column whose language is not in L gets the en-us text. Rows that are not ours keep the exported values.
    """
    with open(export_path, encoding="utf-8-sig", newline="") as f:
        table = list(csv.reader(f))
    header = table[0]
    columns = header[4:]
    ours = {r[0]: r[4:] for r in rows() if not (texts_only and r[0].startswith("DesktopScreenshot"))}
    for row in table[1:]:
        if row and row[0] in ours:
            values = dict(zip(LANGS, ours[row[0]]))
            row[4:] = [values.get(lang, values["en-us"]) for lang in columns]
    return table


if __name__ == "__main__":
    import sys

    check()
    # --texts-only <export> <out.csv>: the screenshot rows keep the exported values (a CSV-only import without images).
    texts_only = "--texts-only" in sys.argv
    args = [a for a in sys.argv[1:] if a != "--texts-only"]
    out = pathlib.Path(args[1]) if len(args) > 1 else HERE / OUT_NAME
    table = fill_export(args[0], texts_only) if args else [["Field", "ID", "Type (Тип)", "default"] + LANGS, *rows()]
    with open(out, "w", encoding="utf-8-sig", newline="") as f:
        csv.writer(f, lineterminator="\r\n").writerows(table)
    print(out, len(table) - 1, "rows, languages:", " ".join(table[0][4:]))
