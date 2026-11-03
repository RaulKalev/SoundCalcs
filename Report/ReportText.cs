using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.Report
{
    /// <summary>Every text the report prints, in English or Estonian.</summary>
    public class ReportText
    {
        public readonly ReportLanguage Language;

        public ReportText(ReportLanguage language)
        {
            Language = language;
            bool et = language == ReportLanguage.Estonian;

            ReportTitle = et ? "Akustiline arvutusaruanne" : "Acoustic calculation report";
            ReportSubtitle = et ? "Helitugevus ja kõne arusaadavus (STI)" : "Sound pressure level and speech intelligibility (STI)";
            Contents = et ? "Sisukord" : "Contents";
            PageOf = et ? "Lk {0} / {1}" : "Page {0} of {1}";
            Project = et ? "Projekt" : "Project";
            ProjectNumber = et ? "Projekti number" : "Project number";
            Client = et ? "Tellija" : "Client";
            Company = et ? "Ettevõte" : "Company";
            Author = et ? "Koostaja" : "Prepared by";
            CheckedBy = et ? "Kontrollis" : "Checked by";
            Revision = et ? "Versioon" : "Revision";
            Date = et ? "Kuupäev" : "Date";
            Description = et ? "Kirjeldus" : "Description";
            Model = et ? "Mudel" : "Model";
            AreasInReport = et ? "Aruandes olevad alad" : "Areas in this report";
            CoverNote = et
                ? "Arvutatud SoundCalcs Revit lisandmooduliga. Tulemused on arvutuslikud ja sõltuvad sisestatud andmetest."
                : "Calculated with the SoundCalcs Revit plugin. Results are predictions and depend on the input data.";

            Summary = et ? "Kokkuvõte" : "Summary";
            SummaryIntro = et
                ? "Ruumide põhitulemused. Keskmine on punktide väärtuste aritmeetiline keskmine kuulaja kõrgusel."
                : "Main results per room. Means are arithmetic means of the grid values at listener height.";
            Area = et ? "Ala" : "Area";
            Room = et ? "Ruum" : "Room";
            FloorArea = et ? "Pind m²" : "Area m²";
            Volume = et ? "Maht m³" : "Volume m³";
            Speakers = et ? "Kõlarid" : "Speakers";
            Points = et ? "Punktid" : "Points";
            Mean = et ? "Keskmine" : "Mean";
            Min = "Min";
            Max = "Max";
            Spread = et ? "Hajuvus" : "Spread";
            P10 = "P10";
            P90 = "P90";
            Result = et ? "Tulemus" : "Result";
            Pass = et ? "Vastab" : "Pass";
            Fail = et ? "Ei vasta" : "Fail";
            NotAssessed = et ? "Ei hinnatud" : "Not assessed";
            NoCriteria = et ? "Nõudeid pole" : "No criteria";
            StiClass = et ? "STI klass" : "STI class";
            Rt60At500 = et ? "T60 500 Hz" : "RT60 500 Hz";
            AreaSummaryLine = et
                ? "{0} ruumi: {1} vastab, {2} ei vasta, {3} hindamata."
                : "{0} rooms: {1} pass, {2} fail, {3} not assessed.";
            AreaSummaryLineOne = et
                ? "1 ruum: {1} vastab, {2} ei vasta, {3} hindamata."
                : "1 room: {1} pass, {2} fail, {3} not assessed.";

            AreaHeading = et ? "Ala: {0}" : "Area: {0}";
            Level = et ? "Korrus" : "Level";
            Calculated = et ? "Arvutatud" : "Calculated";
            Quality = et ? "Arvutuse täpsus" : "Calculation quality";
            QualityDraft = et ? "Kiire (ilma teise järgu ja lae/põranda peegeldusteta)" : "Draft (no second-order or floor/ceiling reflections)";
            QualityFull = et ? "Täielik" : "Full";
            Receivers = et ? "Arvutuspunkte" : "Receiver points";
            RoomsCount = et ? "Ruume" : "Rooms";
            SpeakerLine = et ? "Kõlariliin" : "Speaker line";
            LineBoth = et ? "Mõlemad (A ja B)" : "Both (A and B)";
            Outdated = et
                ? "Tähelepanu: ala sisendeid on pärast arvutust muudetud. Tulemused ei pruugi kehtida — arvuta ala uuesti."
                : "Note: this area's inputs changed after it was calculated. The results may be out of date; run the area again.";
            NotCalculated = et ? "Ala pole arvutatud." : "This area has not been calculated.";
            Notes = et ? "Märkused" : "Notes";
            PlanCaption = et ? "{0} kuulaja kõrgusel {1:0.0#} m. Värviskaala P2–P98." : "{0} at listener height {1:0.0#} m. Colour range P2–P98.";
            IsolinesCaption = et ? " Samajooned: {0}." : " Contour lines: {0}.";

            Inputs = et ? "Lähteandmed" : "Inputs";
            GridSpacing = et ? "Arvutusvõrgu samm" : "Grid spacing";
            ReceiverHeight = et ? "Kuulaja kõrgus" : "Listener height";
            BoundaryOffset = et ? "Kaugus piirist" : "Offset from boundary";
            Temperature = et ? "Temperatuur" : "Temperature";
            Humidity = et ? "Suhteline niiskus" : "Relative humidity";
            Occupants = et ? "Inimeste arv" : "Occupants";
            FloorFinish = et ? "Põrandakate" : "Floor finish";
            CeilingFinish = et ? "Laepind" : "Ceiling finish";
            Rt60Mode = et ? "Järelkõlaaeg" : "Reverberation time";
            Rt60PerRoom = et ? "hinnatud iga ruumi geomeetriast ja materjalidest" : "estimated per room from its geometry and materials";
            Rt60Global = et ? "sisestatud väärtused (kõik ruumid)" : "entered values (all rooms)";
            SpeechSpectrum = et ? "STI testsignaal" : "STI test signal";
            SpeechMale = et ? "IEC 60268-16 meeshääl" : "IEC 60268-16 male speech";
            SpeechFemale = et ? "IEC 60268-16 naishääl" : "IEC 60268-16 female speech";
            RoomShapeModel = et ? "Ruumikuju mudel" : "Room shape model";
            On = et ? "sees" : "on";
            Off = et ? "väljas" : "off";
            Band = et ? "Oktaavriba" : "Octave band";
            BackgroundNoise = et ? "Taustamüra dB" : "Background noise dB";
            Rt60 = et ? "Järelkõlaaeg T60 s" : "Reverberation RT60 s";
            BandsCaption = et ? "Sagedusribade andmed" : "Per-band data";

            Walls = et ? "Seinad" : "Walls";
            LineStyle = et ? "Joonestiil" : "Line style";
            WallType = et ? "Seinatüüp" : "Wall type";
            Stc = "STC";
            Surface = et ? "Pind" : "Surface";
            Height = et ? "Kõrgus" : "Height";
            Segments = et ? "Lõike" : "Segments";
            Length = et ? "Pikkus" : "Length";
            FullHeight = et ? "täiskõrgus" : "full";
            Obstacle = et ? "takistus" : "obstacle";
            NoWalls = et ? "Seinu pole." : "No walls.";

            SpeakerTypes = et ? "Kõlaritüübid" : "Speaker types";
            SpeakerPositions = et ? "Kõlarite asukohad" : "Speaker positions";
            FamilyType = et ? "Perekond : tüüp" : "Family : type";
            Count = et ? "Arv" : "Count";
            Profile = et ? "Profiil" : "Profile";
            OnAxisLevel = et ? "Tase 1 m" : "Level at 1 m";
            Coverage = et ? "Kattenurk" : "Coverage";
            Response = et ? "Sagedusvastus" : "Response";
            ProfileOmni = et ? "Ümarsuunaline" : "Omnidirectional";
            ProfileCone = et ? "Laekõlar (koonus)" : "Ceiling (cone)";
            ProfileWall = et ? "Seinakõlar" : "Wall-mounted";
            ProfileGll = et ? "GLL fail" : "GLL file";
            ProfilePolar = et ? "polaartabel" : "polar table";
            Datasheet = et ? "andmeleht" : "datasheet";
            Id = "ID";
            Type = et ? "Tüüp" : "Type";
            Aim = et ? "Suund" : "Aim";
            Down = et ? "alla" : "down";
            Line = et ? "Liin" : "Line";
            SpeakerTableNote = et
                ? "X ja Y on projekti koordinaadid meetrites; kõrgus on korruse kohal. Suund 0° = +X, vastupäeva."
                : "X and Y are project coordinates in metres; height is above the level. Aim 0° = +X, counter-clockwise.";
            NoSpeakers = et ? "Kõlareid pole." : "No speakers.";

            RoomResults = et ? "Ruumide tulemused" : "Room results";
            Ceiling = et ? "Lae kõrgus" : "Ceiling height";
            Enclosure = et ? "Seintega suletud" : "Enclosed by walls";
            Rt60Used = et ? "T60 (500 Hz)" : "RT60 (500 Hz)";
            Estimated = et ? "hinnatud" : "estimated";
            Entered = et ? "sisestatud" : "entered";
            Quantity = et ? "Suurus" : "Quantity";
            StiDistribution = et ? "STI jaotus punktide kaupa" : "STI distribution over the points";
            CriteriaHeading = et ? "Nõuete kontroll" : "Criteria check";
            Criterion = et ? "Nõue" : "Criterion";
            Target = et ? "Sihtväärtus" : "Target";
            Value = et ? "Väärtus" : "Value";
            ValueChart = et ? "Väärtuste kaart: {0}" : "Value chart: {0}";
            ValueChartCaption = et
                ? "{0} arvutuspunktides (samm {1:0.0#} m). Min ja max on märgitud. Loetavuse huvides on igas suunas näidatud iga {2}. punkt."
                : "{0} at the grid points (spacing {1:0.0#} m). Min and max are marked. To keep the numbers legible, one point in {2} is labelled in each direction.";
            ValueChartCaptionAll = et
                ? "{0} igas arvutuspunktis (samm {1:0.0#} m). Min ja max on märgitud."
                : "{0} at each grid point (spacing {1:0.0#} m). Min and max are marked.";
            Contributions = et ? "Kõlarite panus" : "Speaker contributions";
            ContributionsIntro = et
                ? "Iga kõlari tase (dBA) ruumi keskpunktile lähimas arvutuspunktis ({0:0.0}; {1:0.0}). Summa: {2:0.0} dBA."
                : "Level of each speaker (dBA) at the grid point nearest the room centre ({0:0.0}, {1:0.0}). Sum: {2:0.0} dBA.";
            ContributionsSkipped = et
                ? "Kõlarite panust ei arvutatud: kõlareid on üle {0}."
                : "Speaker contributions were not calculated: more than {0} speakers.";
            Share = et ? "Osakaal" : "Share";
            Speaker = et ? "Kõlar" : "Speaker";
            Distance = et ? "Kaugus" : "Distance";

            Criteria = et ? "Nõuded" : "Criteria";
            CriteriaIntro = et
                ? "Ruume hinnati järgmiste nõuete järgi. Väärtusi saab muuta iga projekti kohta."
                : "Rooms are judged against these criteria. The values can be changed per project.";
            CritStiMin = et ? "STI igas punktis" : "STI at every point";
            CritStiMean = et ? "Ruumi keskmine STI" : "Room average STI";
            CritSplSpread = et ? "SPL hajuvus ruumis (max − min)" : "SPL variation in the room (max − min)";
            CritSplMinA = et ? "Helitugevus igas punktis" : "Sound level at every point";
            CritSnr = et ? "Signaali ja müra suhe igas punktis" : "Signal-to-noise ratio at every point";
            CritCoverage = et ? "Punktinõuded peavad kehtima {0:0} % punktides (halvimad {1:0} % jäetakse välja)." : "Point criteria must hold at {0:0} % of the points (the worst {1:0} % are left out).";
            CritSkipNoSpeakers = et ? "Kõlariteta ruume ei hinnata." : "Rooms without speakers are not assessed.";
            CritStandard = et
                ? "Vaikeväärtused järgivad häälteavitussüsteemide tava (EN 50849, IEC 60268-16)."
                : "The defaults follow common voice-alarm practice (EN 50849, IEC 60268-16).";
            StiScale = et ? "STI skaala (IEC 60268-16)" : "STI scale (IEC 60268-16)";

            Method = et ? "Arvutusmeetod" : "Calculation method";
            MethodParagraphs = et ? new[]
            {
                "Helirõhutase arvutatakse igas võrgupunktis oktaavribades 125 Hz – 8 kHz iga kõlari otsesest helist, peegeldustest ja hajusväljast. Kõlari suunavus tuleb koonusmudelist, andmelehe kattenurkadest või mõõdetud polaartabelist; sagedusvastus kõlaritüübi seadetest.",
                "Peegeldused arvutatakse peegelallikate meetodiga: esimese järgu seinapeegeldused, täielikus režiimis ka teise järgu ning põranda- ja laepeegeldused. Iga tee läbib seinad oma heliisolatsiooniga (STC, ISO 717-1 kontuur ribade kaupa) ning ekraanide servadel arvestatakse difraktsiooni (Kurze–Anderson / Maekawa).",
                "Hajusväli arvutatakse Barroni muudetud teooriaga iga ruumi mahu ja järelkõlaaja järgi. Täisnurksetes ruumides (≥ 90 % ümbritsevast ristkülikust) kasutatakse täielikus režiimis kastruumi peegelallikate võret. Ruumidevaheline heli: L2 = L1 − R + 10·log S/A2 (EN ISO 12354-1).",
                "Kõne edastusindeks STI arvutatakse IEC 60268-16:2011 kaudse meetodiga arvutatud impulsskaja modulatsiooni ülekandefunktsioonist, arvestades taustamüra, kuulmise maskeerimist ja kuulmisläve. C80 ja D50 arvutatakse 500 Hz ja 1 kHz ribadest.",
                "Õhu neeldumine arvestatakse ISO 9613-1 järgi temperatuuri ja niiskuse alusel. Järelkõlaaeg on kas sisestatud või hinnatud Eyringi valemiga ruumi pindade, materjalide, inimeste ja õhu neeldumise järgi.",
                "Tulemused on mudeli ennustused ja sõltuvad sisendandmete täpsusest (kõlarite andmed, materjalid, järelkõlaaeg, taustamüra). Kriitilistes ruumides soovitatakse tulemusi mõõtmisega kontrollida."
            } : new[]
            {
                "Sound pressure level is calculated at every grid point in the octave bands 125 Hz – 8 kHz from each speaker's direct sound, reflections and the diffuse field. Speaker directivity comes from a cone model, datasheet coverage angles or a measured polar table; the frequency response from the speaker type's settings.",
                "Reflections use the image-source method: first-order wall reflections, in Full quality also second-order and floor/ceiling reflections. Every path passes the walls it crosses with their transmission loss (STC, ISO 717-1 reference contour per band), and diffraction around the ends and over the top of screens is included (Kurze–Anderson / Maekawa).",
                "The diffuse field follows Barron's revised theory per room, from its volume and reverberation time. In Full quality, rooms that are (near-)rectangular (≥ 90 % of their fitted box) use a shoebox image-source lattice instead. Sound between rooms: L2 = L1 − R + 10·log S/A2 (EN ISO 12354-1).",
                "The Speech Transmission Index is calculated by the indirect method of IEC 60268-16:2011 from the modulation transfer function of the computed impulse response, with background noise, auditory masking and the reception threshold. C80 and D50 use the 500 Hz and 1 kHz bands.",
                "Air absorption follows ISO 9613-1 for the temperature and humidity. The reverberation time is either entered or estimated with Eyring's formula from the room's surfaces, materials, occupants and air absorption.",
                "Results are predictions of the model and depend on the accuracy of the input data (speaker data, materials, reverberation time, background noise). For critical rooms, verifying the results by measurement is recommended."
            };

            StiBad = et ? "Halb" : "Bad";
            StiPoor = et ? "Nõrk" : "Poor";
            StiFair = et ? "Rahuldav" : "Fair";
            StiGood = et ? "Hea" : "Good";
            StiExcellent = et ? "Väga hea" : "Excellent";
            OutsideRooms = et ? "Väljaspool ruume" : "Outside rooms";
        }

        public readonly string ReportTitle, ReportSubtitle, Contents, PageOf, Project, ProjectNumber, Client, Company, Author,
            CheckedBy, Revision, Date, Description, Model, AreasInReport, CoverNote;
        public readonly string Summary, SummaryIntro, Area, Room, FloorArea, Volume, Speakers, Points, Mean, Min, Max, Spread,
            P10, P90, Result, Pass, Fail, NotAssessed, NoCriteria, StiClass, Rt60At500, AreaSummaryLine, AreaSummaryLineOne;
        public readonly string AreaHeading, Level, Calculated, Quality, QualityDraft, QualityFull, Receivers, RoomsCount,
            SpeakerLine, LineBoth, Outdated, NotCalculated, Notes, PlanCaption, IsolinesCaption;
        public readonly string Inputs, GridSpacing, ReceiverHeight, BoundaryOffset, Temperature, Humidity, Occupants, FloorFinish,
            CeilingFinish, Rt60Mode, Rt60PerRoom, Rt60Global, SpeechSpectrum, SpeechMale, SpeechFemale, RoomShapeModel, On, Off,
            Band, BackgroundNoise, Rt60, BandsCaption;
        public readonly string Walls, LineStyle, WallType, Stc, Surface, Height, Segments, Length, FullHeight, Obstacle, NoWalls;
        public readonly string SpeakerTypes, SpeakerPositions, FamilyType, Count, Profile, OnAxisLevel, Coverage, Response,
            ProfileOmni, ProfileCone, ProfileWall, ProfileGll, ProfilePolar, Datasheet, Id, Type, Aim, Down, Line,
            SpeakerTableNote, NoSpeakers;
        public readonly string RoomResults, Ceiling, Enclosure, Rt60Used, Estimated, Entered, Quantity, StiDistribution,
            CriteriaHeading, Criterion, Target, Value, ValueChart, ValueChartCaption, ValueChartCaptionAll, Contributions,
            ContributionsIntro, ContributionsSkipped, Share, Speaker, Distance;
        public readonly string Criteria, CriteriaIntro, CritStiMin, CritStiMean, CritSplSpread, CritSplMinA, CritSnr,
            CritCoverage, CritSkipNoSpeakers, CritStandard, StiScale;
        public readonly string Method;
        public readonly string[] MethodParagraphs;
        public readonly string StiBad, StiPoor, StiFair, StiGood, StiExcellent, OutsideRooms;

        /// <summary>IEC 60268-16 category name of an STI value.</summary>
        public string StiCategory(double sti) => StiCategoryName(Report.RoomStatisticsCalculator.StiCategory(sti));

        public string StiCategoryName(int category)
        {
            switch (category)
            {
                case 0: return StiBad;
                case 1: return StiPoor;
                case 2: return StiFair;
                case 3: return StiGood;
                default: return StiExcellent;
            }
        }

        /// <summary>Name of a quantity with its unit, e.g. "SPL (dBA)".</summary>
        public string ModeName(VisualizationMode mode)
        {
            bool et = Language == ReportLanguage.Estonian;
            switch (mode)
            {
                case VisualizationMode.SPL: return et ? "Helirõhutase SPL (dB)" : "Sound pressure level SPL (dB)";
                case VisualizationMode.SPL_A: return et ? "A-korrigeeritud helitase (dBA)" : "A-weighted sound level (dBA)";
                case VisualizationMode.STI: return et ? "Kõne edastusindeks STI" : "Speech Transmission Index STI";
                case VisualizationMode.C80: return et ? "Selgus C80 (dB)" : "Clarity C80 (dB)";
                default:
                    int b = Visualization.HeatmapMath.GetOctaveBandIndex(mode);
                    return (et ? "SPL ribas " : "SPL at ") + OctaveBands.Labels[b] + " Hz (dB)";
            }
        }

        /// <summary>Short name of a quantity for table cells and legends, e.g. "dBA".</summary>
        public static string ModeShort(VisualizationMode mode)
        {
            switch (mode)
            {
                case VisualizationMode.SPL: return "SPL dB";
                case VisualizationMode.SPL_A: return "SPL dBA";
                case VisualizationMode.STI: return "STI";
                case VisualizationMode.C80: return "C80 dB";
                default: return "SPL " + OctaveBands.Labels[Visualization.HeatmapMath.GetOctaveBandIndex(mode)] + " Hz";
            }
        }

        /// <summary>Number format for a quantity (STI with two decimals, levels with one).</summary>
        public static string Format(VisualizationMode mode, double value) =>
            double.IsNaN(value) ? "–" : mode == VisualizationMode.STI
                ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
