/** Ladle (MIT): every component of the design system in each state (brief 11 §1). Run: npm run ladle -w @lossrun/ui */
export default {
  stories: "src/stories/**/*.stories.tsx",
  addons: { theme: { enabled: true, defaultState: "light" }, width: { enabled: true, options: { workbench: 1440, tablet: 1024, phone: 375 }, defaultState: 0 }, a11y: { enabled: true } },
  defaultStory: "status--badges",
};
