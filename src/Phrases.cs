using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// Everything the app can say to cheer Morgan on (spec 12.1, 12.2, 12.5, 19.11): phrases grouped by
    /// moment and by roast level (0 gentle, 1 teasing, 2 spicy, 3 savage), holiday sets, unlockable
    /// sets, and the fun facts. No emoji in here: Tone.Big adds those at big moments only.
    /// </summary>
    public static class Phrases
    {
        public sealed record P(string Group, int Level, string Text);

        public static readonly P[] All = Parse(PhraseData);

        public static IReadOnlyList<string> Facts => FactData;

        private static P[] Parse(string data) => data
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.Split('|', 3))
            .Select(a => new P(a[0], int.Parse(a[1]), a[2]))
            .ToArray();

        /// <summary>
        /// The next phrase of a group for this roast level (0 to 3). Phrases at the level and one below
        /// are used; each is spoken once before any repeats, until most of the group has been used.
        /// used remembers what was said (kept in Settings); null when the group has nothing.
        /// </summary>
        public static string? Pick(string group, int dial, Dictionary<string, List<int>> used, Random rng)
        {
            var idx = Enumerable.Range(0, All.Length).Where(i => All[i].Group == group).ToList();
            if (idx.Count == 0) return null;
            var pool = group.StartsWith("Set:") ? idx : idx.Where(i => All[i].Level <= dial && All[i].Level >= dial - 1).ToList();
            if (pool.Count < 3) pool = idx.Where(i => All[i].Level <= dial).ToList();
            if (pool.Count == 0) pool = idx;
            int pick = Rotate(group + "/" + dial, pool, used, rng);
            return All[pick].Text;
        }

        public static string PickFact(Dictionary<string, List<int>> used, Random rng) =>
            FactData[Rotate("Fact", Enumerable.Range(0, FactData.Length).ToList(), used, rng)];

        /// <summary>Picks from the pool without repeating until three quarters of it has been used, then starts over (never repeating the last one at once).</summary>
        private static int Rotate(string key, List<int> pool, Dictionary<string, List<int>> used, Random rng)
        {
            if (!used.TryGetValue(key, out var seen)) used[key] = seen = new List<int>();
            var fresh = pool.Where(i => !seen.Contains(i)).ToList();
            if (fresh.Count <= pool.Count / 4)
            {
                int last = seen.Count > 0 ? seen[^1] : -1;
                seen.Clear();
                fresh = pool.Where(i => i != last || pool.Count == 1).ToList();
            }
            int pick = fresh[rng.Next(fresh.Count)];
            seen.Add(pick);
            return pick;
        }

        private const string PhraseData = """
Done|0|Nice work. One more off the list.
Done|0|That's done, and you did it. Good job.
Done|0|Another one down. Steady and sure.
Done|0|Look at you, getting things done.
Done|0|Done. You can feel a little proud of that.
Done|0|That one's finished. Take a breath if you need it.
Done|0|Well done. The list is a bit lighter now.
Done|0|One step closer. You're doing great.
Done|0|Done and dusted. Nicely handled.
Done|0|Good one. That counts, every bit of it.
Done|0|Quietly getting it done. I like that for you.
Done|1|Look at you, functioning.
Done|1|Down it goes. Who's the productive one now?
Done|1|Another one gone. Show off.
Done|1|Done. Your to-do list is starting to feel nervous.
Done|1|Well, would you look at that. Competence.
Done|1|That's one fewer thing to avoid. Lovely.
Done|1|Checked off. The list never saw it coming.
Done|1|Done. I'd clap, but I'm software.
Done|1|Not bad at all. I'm only a little suspicious.
Done|1|Another win. Try to look surprised.
Done|1|Productivity detected. Please remain calm.
Done|2|Wow, so you can do things when you try. Noted.
Done|2|Done. The procrastination committee is furious.
Done|2|Finished. Your past self, who put it off, owes you an apology.
Done|2|And just like that, the thing you dreaded was fine. Shocking.
Done|2|Dead. The task, not you. Well done.
Done|2|That went down faster than your excuses.
Done|2|Done. Was that so hard? Do not answer that.
Done|2|Look who showed up to her own life today.
Done|2|One more and you'll be unbearable. Keep going.
Done|2|Done. The list is sweating.
Done|2|Okay, overachiever. Save some for tomorrow.
Done|3|Done already? Bow before your own greatness, you menace.
Done|3|Oh, you magnificent disaster, you actually did it.
Done|3|Finished. Somewhere a task is weeping in the corner.
Done|3|Crush it like it owes you money. Oh wait, you just did.
Done|3|You are a glorious chaos goblin and that task never stood a chance.
Done|3|Done. The list begged for mercy and you showed none.
Done|3|Tell the others you're the one to fear. Another one gone.
Done|3|Try not to let it go to your head. Too late, I can see the crown.
Done|3|Ruthless. Efficient. Faintly terrifying. Do it again.
Done|3|Imagine being a task and meeting you. Brutal.
Done|3|Fine, you're a little bit brilliant. Do not let it get out.
Half|0|Half your day is done. Genuinely proud of you.
Half|0|You're past the halfway mark. Keep that gentle pace.
Half|0|Halfway there. That's real progress.
Half|0|Half done already. Well done.
Half|1|Halfway. The hard part is behind you, probably.
Half|1|Half the list is gone. The other half is nervous.
Half|1|Midpoint reached. Snack break, maybe?
Half|1|Half your day is done. Look at you, being a person with a plan.
Half|2|Halfway done. The second half can't be worse than the first. Probably.
Half|2|Half down. At this rate you might actually have an evening.
Half|2|Half the list, gone. The other half is just showing off its deadlines.
Half|2|You crossed the halfway line, and nobody even had to nag you.
Half|3|Half the list is rubble. Keep swinging, you terror.
Half|3|Halfway. The remaining tasks are shaking. Good.
Half|3|You're half done and still standing. Dramatic. I approve.
Half|3|Half of your enemies have fallen. The rest can run.
All|0|Everything for today is done. You did that. Rest well.
All|0|All done. What a good day's work.
All|0|That's the whole list. Be kind to yourself tonight.
All|0|Every last one is done. I'm proud of you.
All|0|All finished. You've earned some quiet.
All|1|All done. Who are you, and what have you done with the procrastinator?
All|1|The list is empty. I'm a little emotional.
All|1|Everything done. Go be insufferably relaxed.
All|1|That's all of it. Gold star, and I don't even have stars.
All|1|Clean sweep. I'd throw a parade, but, again, software.
All|2|All done. The list has been defeated. Humiliated, even.
All|2|Everything's finished. Please do not take this as a sign you have to do it again tomorrow. You will, though.
All|2|You cleared the board. Smug is allowed. Encouraged, even.
All|2|Done with all of it. Now go lie down and be insufferable about it.
All|2|Zero left. Honestly, a little scary how good you are at this.
All|3|The list is dead. Long live you, the terror of Tuesday.
All|3|Total victory. Bow, task list, bow.
All|3|All done. You came, you saw, you made the to-do list cry.
All|3|Wiped out the whole list. Fear the bunny.
All|3|Everything is done and you're still standing. Unfair to the rest of us.
Haunting|0|That one had been on the list a long time. It's finally done, and that's a real relief.
Haunting|0|You finished the one that kept coming back. That took courage.
Haunting|0|The old one is done. You can let that weight go now.
Haunting|1|That one's been haunting the list for ages. It's gone now.
Haunting|1|The ghost task has been laid to rest. Rest in peace, finally.
Haunting|1|Finally. That one has been staring at you for ages.
Haunting|2|You finally did the thing you've been dodging. Took long enough, and I'm proud anyway.
Haunting|2|That task has been squatting on your list for weeks. Eviction complete.
Haunting|2|It only took you forever. Still counts. Still great.
Haunting|3|The ancient horror is slain. Tell the bards.
Haunting|3|You dodged that one for ages and then destroyed it. Villain behavior.
Haunting|3|That task was older than some of your bad decisions. Gone now.
Haunting|3|Exorcism complete. The list is lighter and slightly less haunted.
First|0|First one of the day is done. That's the hardest one, and you did it.
First|0|You've started. The rest gets easier from here.
First|0|One down already. A good start.
First|1|First one done. Momentum, meet your new friend.
First|1|Day started. The bar was low and you cleared it. Nice.
First|1|There it is. The first one of the day. Look at you go.
First|2|First task down. Imagine what you could do with a second.
First|2|You got going without being nagged. Suspicious, but good.
First|2|The first one is done, so now there's no excuse to stop.
First|3|First blood. The list should be nervous.
First|3|Day one of your reign, and the first target is toast.
First|3|The first task is down. Do keep going, I'm enjoying this.
Comeback|0|Welcome back. One done today, and that's all that counts.
Comeback|0|You're back at it. A missed day doesn't matter, you're here now.
Comeback|0|Good to see you again. That first one back is the hardest, and it's done.
Comeback|1|Back after a day off. The list missed you, and I did too.
Comeback|1|Missed a day, and yet here you are. Comeback queen.
Comeback|1|Yesterday was a break. Today is a win. Balanced.
Comeback|2|Yesterday happened. Today you did something about it. Good.
Comeback|2|Skipped a day, came back swinging. Dramatic, and I love it.
Comeback|2|The list waited. You returned. Bonus points for the entrance.
Comeback|3|She returns. The list trembles.
Comeback|3|A day away and you still came back meaner. Good.
Comeback|3|Back from the dead and already crossing things off. Showstopper.
Finals|0|Finals week is long, and you're handling it. One thing at a time.
Finals|0|Study a bit, rest a bit. You're doing fine.
Finals|0|You've prepared more than you think. Keep going.
Finals|0|Almost through finals. Be gentle with yourself.
Finals|1|Finals week and you're still checking things off. Impressive, honestly.
Finals|1|Finals brain is real. Done is done though.
Finals|1|Coffee, courage, and a checked box. Nice.
Finals|1|The finish line is in sight. Don't trip.
Finals|2|Finals week: where sleep goes to die. Good job getting that done anyway.
Finals|2|Every exam is just a long quiz. Which you'd also pass. Keep going.
Finals|2|Done. Now the professors can't say you didn't try.
Finals|2|Your future self, post-finals, will be so smug. Keep earning it.
Finals|3|Finals can't touch you. Another one crushed.
Finals|3|Study harder, you glorious menace. The exam is already scared.
Finals|3|One more piece of finals week conquered. Burn it down.
Finals|3|You're a finals week villain, and the villain is winning.
Halloween|0|Happy Halloween season. One more task laid to rest.
Halloween|1|Spooky season, and the only scary thing is how productive you are.
Halloween|1|Done. The ghost tasks are shaking.
Halloween|2|Another task is dead. Perfect for Halloween.
Halloween|2|Boo. That's what that task said right before you finished it.
Halloween|3|The tasks are haunted, and you are the exorcist. Delightful.
Thanksgiving|0|Done. Something to be thankful for, and it's you.
Thanksgiving|1|Done. Grateful for that. Now, pie.
Thanksgiving|1|One more thing off the list. The turkey would be proud.
Thanksgiving|2|Stuffed with productivity, just like the turkey will be stuffed.
Thanksgiving|2|Another task gone, and the pie is getting closer.
Thanksgiving|3|Gobble gobble. That task was devoured.
Winter|0|Done. Happy holidays, and well done.
Winter|1|Cozy and productive. That's the dream.
Winter|1|Done. Hot chocolate is now earned.
Winter|2|One task wrapped up. Fancy.
Winter|2|The holidays are coming and the list is shrinking. Santa approves.
Winter|3|On the nice list, barely, but a task down is a task down.
Valentine|0|Done. Be sweet to yourself today.
Valentine|1|You and your to-do list: a love story with a happy ending, finally.
Valentine|1|Done. The list sends its regards. It's seeing other people.
Valentine|2|Another one down. You are definitely my favorite task-crusher.
Valentine|2|Roses are red, tasks are due, this one is done, I'm proud of you.
Valentine|3|Love is a battlefield and you just won this skirmish.
Surprise|0|Surprise! You're doing better than you think.
Surprise|0|Bonus message: you're allowed to take a break after this.
Surprise|0|Hidden message: you're really good at this.
Surprise|1|Surprise! Remember when you thought this would take forever? Cute.
Surprise|1|Bonus roast: your list is still longer than your attention span. But you're beating it.
Surprise|1|Plot twist: you're competent. Don't tell anyone.
Surprise|2|Surprise roast: you spent more time avoiding that than doing it. Classic.
Surprise|2|Fun fact about you: you work better when nobody's looking. So I'm looking away.
Surprise|2|Secret message: your procrastination has a fan club. You have a bigger one.
Surprise|3|Surprise! A tiny crown for you, you chaotic overachiever.
Surprise|3|Bonus roast: you are a delight and a hazard. Please continue.
Surprise|3|Hidden message: even the Zodiac Academy rivals would have to respect that one. Barely.
Set:Drama|1|Ladies and gentlemen, she has completed the task.
Set:Drama|1|And scene. Applause, applause.
Set:Drama|1|A standing ovation, for a checkbox.
Set:Drama|1|Oh, the drama of a completed to-do. Magnificent.
Set:Drama|1|Take a bow, darling. That was a performance.
Set:Sports|1|And she scores. The crowd goes wild.
Set:Sports|1|That's a clean finish. The commentators are speechless.
Set:Sports|1|Another one in the back of the net.
Set:Sports|1|Instant replay: she crushed it.
Set:Sports|1|What a play. Call it a highlight.
Set:Wizard|1|A spell well cast. The task is vanished.
Set:Wizard|1|Ten points to your house for that one.
Set:Wizard|1|By the stars, it is done.
Set:Wizard|1|The prophecy was right. You were destined to finish this.
Set:Wizard|1|Abracadabra, the task is gone, no wand needed.
Set:Space|1|Mission accomplished, captain. Houston is pleased.
Set:Space|1|Task jettisoned into the void. Smooth flying.
Set:Space|1|One small step for you, one giant leap for your list.
Set:Space|1|Orbit achieved. The task has been left behind.
Set:Space|1|Engage. Task complete.
Set:Pirate|1|Arr, that task has walked the plank.
Set:Pirate|1|Another treasure claimed. Yarr.
Set:Pirate|1|Hoist the flag, matey, it be done.
Set:Pirate|1|Shiver me timbers, that was quick work.
Set:Pirate|1|The seven seas of homework have shrunk by one.
""";

        // True, well-known and safe, for the surprise rewards (spec 12.5).
        private static readonly string[] FactData =
        {
            "Honey never spoils. Edible honey has been found in ancient Egyptian tombs.",
            "Octopuses have three hearts and blue blood.",
            "Bananas are berries, but strawberries are not.",
            "A day on Venus is longer than a year on Venus.",
            "Wombat poop is cube-shaped, so it does not roll away.",
            "Sharks are older than trees.",
            "A group of flamingos is called a flamboyance.",
            "Sea otters hold hands while they sleep so they do not drift apart.",
            "Cows have best friends and get stressed when they are separated.",
            "The Eiffel Tower is a few inches taller in summer, because metal expands in the heat.",
            "Scotland's national animal is the unicorn.",
            "There are more ways to shuffle a deck of cards than there are atoms on Earth.",
            "A bolt of lightning is several times hotter than the surface of the sun.",
            "Butterflies taste with their feet.",
            "Some snails can sleep for years at a time.",
            "The shortest war in history, between Britain and Zanzibar in 1896, lasted less than an hour.",
            "Oxford University is older than the Aztec Empire.",
            "Sloths can hold their breath longer than dolphins can.",
            "Koalas have fingerprints that are almost impossible to tell from human ones.",
            "The Sahara desert was once green, with lakes and grassland.",
            "A teaspoon of a neutron star would weigh billions of tons.",
            "There is a jellyfish that can turn back into a baby jellyfish and start over.",
            "Crows can recognize individual human faces and hold grudges.",
            "Cats spend about two thirds of their lives asleep.",
            "Dolphins call each other by name, using signature whistles.",
            "Elephants are one of the few large animals that cannot jump.",
            "A snail has thousands of tiny teeth.",
            "You are slightly taller in the morning than in the evening.",
            "Your stomach lining replaces itself every few days.",
            "Your bones are alive. They constantly rebuild themselves.",
            "Your heart beats about 100,000 times a day.",
            "A blue whale's tongue can weigh as much as an elephant.",
            "A pineapple takes about two years to grow.",
            "Peanuts are not nuts. They are legumes, like beans.",
            "Carrots were originally purple or yellow, not orange.",
            "Apples float because about a quarter of an apple is air.",
            "The Aztecs used cacao beans as money.",
            "Cleopatra lived closer in time to the Moon landing than to the building of the Great Pyramid.",
            "The first computer bug was a real moth, found stuck in a relay in 1947.",
            "The first webcam was pointed at a coffee pot at Cambridge, so people could see if there was coffee.",
            "The first email was sent in 1971.",
            "The word robot comes from a Czech word meaning forced labor.",
            "The dot over a lowercase i is called a tittle.",
            "Typewriter is the longest word you can type using only the top row of letters.",
            "Louis Braille developed braille when he was fifteen years old.",
            "Venus spins backwards compared to most of the planets.",
            "Sunlight takes about eight minutes to reach Earth.",
            "Saturn is less dense than water, so in a big enough bathtub it would float.",
            "Footprints on the Moon will last for millions of years, because there is no wind to erase them.",
            "There are more trees on Earth than there are stars in the Milky Way.",
            "Antarctica is technically a desert, because it gets so little precipitation.",
            "Russia has eleven time zones.",
            "Canada has more lakes than any other country.",
            "Honeybees dance to tell each other where the flowers are.",
            "A group of crows is called a murder, and a group of owls is a parliament.",
            "Gentoo penguins sometimes give each other pebbles as gifts.",
            "Giraffes have the same number of neck bones as you do: seven.",
            "Starfish have no brain.",
            "Hummingbirds are the only birds that can fly backwards.",
            "Bats are the only mammals that can truly fly.",
            "Rats laugh when they are tickled, at a pitch people cannot hear.",
            "A dog's nose print is as unique as a human fingerprint.",
            "Lightning strikes somewhere on Earth dozens of times every second.",
            "Sound travels about four times faster through water than through air.",
            "Wombats, koalas and kangaroos all carry their babies in pouches, and wombat pouches face backwards.",
            "A day on Mars is only about forty minutes longer than a day on Earth.",
        };
    }
}
