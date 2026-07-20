namespace RPG.Stats
{
    /// <summary>
    /// Central registry of stat IDs as constants.
    /// Core stats: 1-999. Mods should use 10000+ to avoid collisions.
    /// </summary>
    public static class StatIds
    {
        // --- Core Attributes ---
        public const int Strength     = 10;
        public const int Endurance    = 20;
        public const int Dexterity    = 30;
        public const int Discipline   = 40;
        public const int Cunning      = 50;
        public const int Intelligence = 60;
        
        // --- Skills ---
        public const int Melee        = 100;
        public const int Deflection   = 101;
        public const int Gymnastics   = 102;
        
        public const int Grappling    = 200;
        public const int Wayfaring    = 201;
        public const int Handling     = 202;
        
        public const int Marksman     = 300;
        public const int Evasion      = 301;
        public const int Touch        = 302;
        
        public const int Apothecary   = 400;
        public const int Smithcraft   = 401;
        public const int Thaumaturgy  = 402;
        
        public const int Mercantile   = 500;
        public const int Rapport      = 501;
        public const int Performance  = 502;
        
        public const int Elemention   = 600;
        public const int Phasing      = 601;
        public const int Domination   = 602;
        
        // --- Pools ---
        public const int Health       = 1000;
        public const int Stamina      = 1100;
        public const int Focus        = 1200;

        // --- Derived Combat ---
        public const int MaxHealth    = 10000;
        public const int MaxStamina   = 10001;
        public const int MaxMagicka   = 10002;
        public const int PhysDamage   = 10010;
        public const int MagicDamage  = 10011;
        public const int Armor        = 10020;
        public const int MagicResist  = 10021;

        // --- Movement / Utility ---
        public const int MoveSpeed    = 20000;
        public const int CarryWeight  = 20001;
    }
}