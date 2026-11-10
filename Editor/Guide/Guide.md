---
id: release
title: Make a change live
icon: rocket
---
Every change goes through four stages. Do the steps from top to bottom. A picture on a grey square is a button on the package's row in the Content Browser.

::: stage computer | Your computer | Only you see it.
button: arrow-down-to-line | Pull | Get everyone's latest work before you start, on every computer you use. | The button only shows when there is something new. Check out pulls by itself.
button: folder-down | Check out | Get the package you want to change (blue button). You only need the packages you work on.
step: pencil | Change it and try it | Change it in Unity and press Play. Unity uses your own files for every package you have checked out.
:::

::: stage team | Team | Teammates get it when they pull. | push
button: arrow-up-from-line | Commit & push | Write a short note about what you changed. Now the team has it. | If the bar at the bottom of the Content Browser says the push failed, your work is still only on your computer. Pull, then ask for help.
:::

::: stage test | Test version | {{test-version-audience: Builds that load staging show it. Also called staging.}} | upload
button: hammer | Build | Packs the package for the game.
button: cloud-upload | Upload | Puts it into the test version. The green staging badge gets a tick when it matches what you pushed. | It can take a minute or two to arrive.
step: circle-check | Check it | {{check-it: Play it in a build that loads the test version.}} | {{check-it-tip: Unity keeps using your own copy while the package is checked out, so a build is the real check.}}
:::

::: stage live | Players | Every player gets it. Also called production. | promote
button: rocket | Promote | Makes the test version live. Players get it the next time they start the game. | Only promote what you checked, and use the rocket on the package's own row: the one at the top promotes every checked-out package.
:::

---
id: versions
title: Which version do I see?
icon: globe
---
Every package exists in three versions.

| Version | What it is |
|---|---|
| <copy>your own copy</copy> | The files on your computer, also changes you have not pushed yet. Unity uses them for every package you have checked out. |
| <test>test version</test> (staging) | What was uploaded last. |
| <live>live version</live> (production) | What was promoted last. Players get this one. |

The test version only changes when someone uploads. The live version only changes when someone promotes. Which version a build loads is set up for each project.

---
id: together
title: Working together
icon: folder-sync
---
Several people change content at the same time. These habits keep everyone's work safe.

- Pull before you start, and push when you finish something. Small, frequent pushes avoid trouble.
- Working on two computers? Pull on each one before you change anything.
- Tell the team when you work on a package, so two people do not change the same files at once.
- Only discard changes you made yourself.
- A package shows the red **repo lost changes** badge? Do not build, upload or promote it until the lost work is back.

> Something looks wrong in the Content Browser?
> [Badges & warnings](#warnings)

---
id: warnings
title: Badges & warnings
icon: triangle-alert
---
Hover over a badge in the Content Browser to see more about it.

## Badges on a package

| You see | What it means | What to do |
|---|---|---|
| <warn>2 to pull</warn> | Teammates pushed changes you do not have yet. | Pull. |
| <test>staging</test> or <live>production</live> with a tick | The test (green) or live (blue) version matches what is pushed. | Nothing. |
| <warn>· 3 behind</warn> | Newer work is pushed but not uploaded yet (staging) or not promoted yet (production). | Build and upload, or promote once you have checked it. |
| <warn>· differs</warn>, <warn>· unverified</warn> | The uploaded version does not match pushed work. | Build and upload again. |
| <warn>· not in repo</warn> | Someone uploaded changes that were never pushed. | Ask whoever uploaded it to push, then build and upload again. Do not promote it. |
| <warn>· removed</warn> | The package was deleted or renamed. | Ask before you change it. |
| <warn>⚠</warn> on a package you do not have | It is live, but not in the test version. | Ask before you change it. |
| <error>repo lost changes</error> | A merge threw away someone's work. | Click it to see whose work and to put it back. Do not build, upload or promote the package until it is back and pushed. |

## Banners at the top

| You see | What it means | What to do |
|---|---|---|
| <warn>…in the middle of a merge</warn> | Your work and a teammate's are being combined. | Do not discard anything. Finish it in your git client, or click Abort and ask for help. |
| <warn>…unresolved conflicts</warn> | Two changes to the same file clashed. | Do not discard anything. Ask for help. |
| <warn>Unity version changed</warn> | This project was opened with a different Unity version. | Do not click Bump generation. Close Unity and ask a programmer. |
| <warn>…has not been initialized</warn> | This computer has no content set up yet. | See First time on a computer. |

## Bar at the bottom

| You see | What it means | What to do |
|---|---|---|
| <warn>⚠ …push failed</warn> | Your commit is still only on your computer. | Pull, then ask for help. |

---
id: first-time
title: First time on a computer
icon: circle-plus
---
Only needed if you want to change content.

- Open the Content Browser: **Window > Content Browser**.
- It asks to initialize? Click **Initialize**. This takes a while the first time.
- Check out the packages you want to change.
- To upload or promote you need upload access on that computer. Ask whoever looks after the content storage to set it up (Project Settings > Content Repo > Configure credentials).

[Make a change live](#release)
